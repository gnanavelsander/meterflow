# MeterFlow

MeterFlow is a C# backend for importing hourly energy readings and calculating daily consumption per building. It validates each CSV before saving it, runs analysis as a background job, and returns daily totals with the hours that are missing. A recorded zero counts as a measurement. A day without readings has `null` consumption.

One ASP.NET Core application hosts the API and an `AnalysisWorker`. PostgreSQL stores users, projects and measurements as well as the job queue and results. The main implementation is under [src/MeterFlow.Api](src/MeterFlow.Api).

## From CSV to result

After signing in and creating a project, a client uploads a CSV and submits a separate analysis request for that dataset.

| Step | Implementation |
| --- | --- |
| Check access and receive the upload | [DatasetEndpoints.MapDatasets](src/MeterFlow.Api/Datasets/DatasetEndpoints.cs) checks the project's owner and accepts the file as a raw `text/csv` body. |
| Validate and save | [CsvImport.ReadAsync / Parse](src/MeterFlow.Api/Datasets/CsvImport.cs) checks the size, columns and readings. The upload handler saves the dataset metadata and all measurements in one transaction. |
| Request an analysis | [JobEndpoints.MapJobs](src/MeterFlow.Api/Jobs/JobEndpoints.cs) validates the dataset and date range, then saves a job and returns HTTP 202. An `Idempotency-Key` lets a client repeat the request without creating another job. |
| Reserve a job | [JobQueue.Claim](src/MeterFlow.Api/Jobs/JobQueue.cs) locks and updates one eligible row in PostgreSQL. Its lease is a reservation that expires after 30 seconds unless the worker renews it. |
| Calculate and store the result | [JobProcessor.Process](src/MeterFlow.Api/Jobs/JobProcessor.cs) loads the readings and calls [AnalysisCalculator.Calculate](src/MeterFlow.Api/Analysis/AnalysisCalculator.cs). `JobQueue.Complete` saves the JSON result and success status together, provided the worker still holds the lease. |
| Read or download | The routes in [JobEndpoints](src/MeterFlow.Api/Jobs/JobEndpoints.cs) check the job's owner before returning status, JSON or CSV. Results become available when the job succeeds. |

Storing jobs in PostgreSQL keeps accepted work available after an API restart. A queued job can be claimed by the next worker. If a worker stops during processing, its unfinished job can be claimed again after the lease expires, up to three attempts. The new worker recalculates the result from the saved readings. [Architecture](docs/architecture.md) explains the SQL conditions that prevent an old worker from overwriting it.

## Read the code

Start with the [code guide](docs/code-guide.md), which follows the requests through their handlers and database writes. [Program.cs](src/MeterFlow.Api/Program.cs) connects authentication, routes and the worker. [ProjectEndpoints](src/MeterFlow.Api/Projects/ProjectEndpoints.cs) is a smaller example of an authenticated route, and [AppDbContext](src/MeterFlow.Api/Data/AppDbContext.cs) defines the relationships and indexes.

The [data contract](docs/data-contract.md) covers UTC hour endings, partial totals and CSV validation. Imports are limited to 5 MiB, 50,000 readings and 100 buildings. Each analysis covers 1 to 31 complete UTC days.

[Tests](tests/MeterFlow.Tests) include PostgreSQL integration tests for ownership, concurrent job claims and recovery after an interrupted attempt. The [test guide](docs/testing.md) records which checks ran locally and which ran in GitHub Actions. It also collects the current limits and deployment work still needed.

## Run locally (optional)

Use Docker with Compose v2, Python 3 and Git. These commands initialize a new checkout. Keep the existing `.env` password when reusing a database volume.

```sh
git clone https://github.com/gnanavelsander/meterflow.git
cd meterflow
cp .env.example .env
python3 -c 'import pathlib,secrets; pathlib.Path(".env").write_text("POSTGRES_PASSWORD=" + secrets.token_hex(24) + "\n")'
docker compose up --build -d
curl --fail http://localhost:8080/health/ready
```

Once the health endpoint responds successfully, run `python3 scripts/demo.py` or open [Scalar](http://localhost:8080/scalar/v1), the interactive API reference. The API listens on localhost and PostgreSQL stays inside the Compose network. No external service or cloud account is needed.

The [walkthrough](docs/demo.md) includes the HTTP request collection, expected results and restart commands. For building and testing with the .NET SDK, see [test commands](docs/testing.md#run-the-tests).
