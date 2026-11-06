# Following a request through the code

Start in [Program.cs](../src/MeterFlow.Api/Program.cs). It registers services through dependency injection, which supplies handlers and workers with their dependencies, and maps the authenticated `/api` routes. It also registers `AnalysisWorker` as a hosted service, so the API and worker share a process.

[ProjectEndpoints.MapProjects](../src/MeterFlow.Api/Projects/ProjectEndpoints.cs) is a small route to read first. The POST handler validates the name and description, assigns the authenticated user's ID as owner, saves the project and returns 201 with its address. The GET handlers filter by that owner and support name search and pagination.

The rest of the flow uses separate HTTP requests for importing data, requesting an analysis and retrieving its result.

## 1. Upload and validate a CSV

`POST /api/projects/{projectId}/datasets` enters the upload handler in [DatasetEndpoints.MapDatasets](../src/MeterFlow.Api/Datasets/DatasetEndpoints.cs). It checks project ownership before reading the body, then validates the filename and content type. `CsvImport.ReadAsync` reads the raw CSV with a 5 MiB limit, including when the client does not supply a content length.

In [CsvImport.Parse](../src/MeterFlow.Api/Datasets/CsvImport.cs), CsvHelper parses the records. The method checks the header, building identifier, UTC timestamp and decimal consumption. A set of `(building, timestamp)` pairs detects duplicate readings. Errors carry row numbers and reject the whole upload.

Only a successful parse reaches `SaveChangesAsync` in the handler. It adds the dataset metadata and measurements to one `AppDbContext`, then commits them in one transaction. Follow [EnergyDataset and Measurement](../src/MeterFlow.Api/Datasets/EnergyDataset.cs) to [AppDbContext.OnModelCreating](../src/MeterFlow.Api/Data/AppDbContext.cs) to see the foreign key, composite measurement key and decimal precision. The response returns metadata and a dataset ID, which the client uses in its analysis request.

## 2. Save the analysis request

The POST handler in [JobEndpoints.MapJobs](../src/MeterFlow.Api/Jobs/JobEndpoints.cs) accepts a dataset ID and two UTC dates. It verifies that the dataset belongs to the current user and that the selected range covers 1 to 31 days.

The required `Idempotency-Key` identifies a submission for that user. Read the existing-job lookup together with the `DbUpdateException` catch block. The lookup handles ordinary repeats, while the unique database index handles simultaneous inserts. Matching settings return the same job. A key already used with different settings returns 409.

[AnalysisJob](../src/MeterFlow.Api/Jobs/AnalysisJob.cs) records the dataset, dates and analysis version. It also holds the lease fields and result JSON. The response is 202 with a status address, so the client can stop waiting for the POST and poll that address instead.

## 3. Claim and process the job

Next read [JobQueue.Claim](../src/MeterFlow.Api/Jobs/JobQueue.cs). Its SQL selects and updates a row in one statement, assigning a token and an expiry to the reservation. The [architecture guide](architecture.md#reservation-processing-and-retries) explains the row lock and the checks used when renewing or completing the job.

[AnalysisWorker.ExecuteAsync](../src/MeterFlow.Api/Jobs/JobProcessor.cs) calls `Claim` in a loop. Once it receives a job, it starts `Heartbeat` to renew the lease and passes the job to `JobProcessor.Process`. The heartbeat has its own database context because EF contexts cannot execute concurrent operations safely.

The processor loads the saved readings and calls [AnalysisCalculator.Calculate](../src/MeterFlow.Api/Analysis/AnalysisCalculator.cs). The calculator has no HTTP or database dependencies. It builds a lookup by building and timestamp, then checks the 24 expected hour endings for each building and selected day. A missing lookup entry contributes a timestamp to `MissingHourEnds`. A recorded zero increments `MeasuredHours`, even though it adds nothing to the sum.

After calculation, `JobQueue.Complete` checks the lease token and expiry while writing the result. Read `Renew` and `Fail` beside it: all three methods reject updates from a worker whose reservation is no longer valid.

## 4. Read status and results

The GET handlers in [JobEndpoints](../src/MeterFlow.Api/Jobs/JobEndpoints.cs) use `Owned` to load a job for the current user. `View` selects status fields for the response, including attempts, settings and result addresses. `RequireResult` returns 409 until the job has succeeded and contains result JSON.

The JSON endpoint returns the stored result. The CSV endpoint deserializes it and writes one row per building and day, leaving the consumption cell empty when the value is `null`. It does not rerun the analysis.

## Where to look next

| Topic | Starting point |
| --- | --- |
| HTTP errors and pagination limits | [ApiExceptionHandler.TryHandleAsync and Paging.Validate](../src/MeterFlow.Api/Infrastructure/ApiFault.cs) |
| OpenAPI examples and bearer authentication fields | [OpenApiGuide.Configure](../src/MeterFlow.Api/Infrastructure/OpenApiGuide.cs) |
| Test database creation and cleanup | [PostgresFixture and ApiFactory](../tests/MeterFlow.Tests/PostgresFixture.cs) |
| Competing claims and interrupted attempts | [JobApiTests](../tests/MeterFlow.Tests/JobApiTests.cs) |
| Complete HTTP sequence | [scripts/demo.py](../scripts/demo.py) and [examples/meterflow.http](../examples/meterflow.http) |

## Interview preparation

Use the code and tests to explain these decisions and their consequences:

- Trace a midnight reading into the previous day's total. Show how the test detects an incorrect day assignment.
- Distinguish a measured zero, a partial sum and a day without readings using the result fields.
- Find the ownership condition for each resource, including the CSV export.
- Walk through two simultaneous requests with the same idempotency key, from the first lookup to the unique-index conflict.
- Describe what has committed when the client receives 202, and what remains for the worker to do.
- Explain how the database row lock ends before calculation while the lease continues to reserve the job.
- Trace a crash before calculation, during publication and after a successful commit whose response is lost.
- Show why another calculation can occur after interruption without producing another result row. Include the expired-token checks.
- Explain which PostgreSQL behaviors require a real database in the tests.
- Describe how the dataset ID and analysis version identify an analysis, and what would need to change if measurements became editable.
- Relate the import and date-range limits to the in-memory calculation. Outline the changes larger datasets would require without claiming unmeasured capacity.
- Review the [deployment and maintenance limits](testing.md#deployment-and-maintenance) before discussing public operation.
