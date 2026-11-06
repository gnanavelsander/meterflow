# Tests and current limits

## Run the tests

Use the .NET 10 SDK selected by [global.json](../global.json). Run these commands from the repository root with Docker running:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

[PostgresFixture.InitializeAsync](../tests/MeterFlow.Tests/PostgresFixture.cs) uses Testcontainers to start `postgres:17`. `ApiFactory.Create` gives each integration test a separate database, and application startup applies the committed migrations. `DisposeDatabase` drops that test database when the test finishes. The locking and concurrent-insert tests run against PostgreSQL, not an in-memory substitute.

To use an existing test instance, set `METERFLOW_TEST_DB` to its Npgsql connection string. The database role must be able to create and drop databases. Use a test-only instance because the harness creates and drops randomly named `test_*` databases. It expects the role and server to exist already.

Without Docker or PostgreSQL, run only the unit tests:

```sh
dotnet test -c Release --filter 'Category!=Integration'
```

These tests cover parsing and calculation rules. They do not exercise database behavior.

## What the tests check

| Source | Checks |
| --- | --- |
| [CsvImportTests](../tests/MeterFlow.Tests/CsvImportTests.cs) | Header and row validation, malformed quoting, invalid numbers and timestamps, negatives, duplicates, encoding, byte and row limits, and preservation of zero readings. |
| [AnalysisTests](../tests/MeterFlow.Tests/AnalysisTests.cs) | Assignment of midnight readings to the previous day, daily totals, zero versus missing readings, partial completeness and retry classification. |
| [ApiTests](../tests/MeterFlow.Tests/ApiTests.cs) | Identity login, project ownership, pagination, health endpoints, OpenAPI and Problem Details. |
| [DatasetApiTests](../tests/MeterFlow.Tests/DatasetApiTests.cs) | Ownership checks on upload and metadata access, and rejection without creating a dataset. |
| [JobApiTests](../tests/MeterFlow.Tests/JobApiTests.cs) | Simultaneous idempotent submissions, conflicting settings, validation before acceptance, access to jobs and exports, competing claims, rejected stale-worker updates, retry limits and processing a persisted job in a new application instance. |
| [scripts/demo.py](../scripts/demo.py) | HTTP requests from registration through CSV export against the Compose image, checked against the sample answers. With `--verify-restart`, it also retrieves the existing result after restarting the API. |

`Concurrent_claims_and_crash_recovery_fence_stale_workers` expires a reserved job's lease in PostgreSQL, claims it with a new worker and verifies that the old token can no longer renew, complete or fail the job. It exercises the recovery state transition without killing a process at an arbitrary instruction.

`Hosted_worker_processes_a_previously_queued_job` starts another application instance over the same database and waits for its hosted worker to finish a queued job. The Compose restart check covers a different case: it restarts the API after success and confirms that the original token still retrieves the same result.

## GitHub Actions

The [CI workflow](../.github/workflows/ci.yml) has separate test and demo jobs. The test job restores locked dependencies, builds in Release mode and runs the suite against PostgreSQL. It uploads TRX test reports. The demo job builds the container image, starts the Compose stack and runs `python3 scripts/demo.py --verify-restart`.

Successful demo runs upload the synthetic JSON and CSV results. Failed runs collect service logs. CI generates a temporary database password for each run, and the scripts keep credentials and bearer tokens out of their printed output.

## Recorded verification

The first complete implementation is recorded at commit [`b04cba9`](https://github.com/gnanavelsander/meterflow/commit/b04cba9006dfa33b336e49cde2d9c2c8e8d2336b). Its [main-branch CI run](https://github.com/gnanavelsander/meterflow/actions/runs/42200002072) passed both jobs.

| Environment | Completed checks |
| --- | --- |
| Local development machine | Release build, 17 unit tests, OpenAPI and Scalar HTTP checks, Compose configuration and documentation links. |
| GitHub Actions | All 26 tests, including PostgreSQL integration tests, plus the container build, complete HTTP demo and restart check. |

The local Docker daemon was unavailable for that verification. The full database workflow was verified in GitHub Actions, not locally. [Workflow history](https://github.com/gnanavelsander/meterflow/actions/workflows/ci.yml) and pull request checks show subsequent runs and the commits they tested.

## Data and processing limits

Imports accept hourly interval energy in UTC. They do not convert cumulative meter values or local times, and the analysis does not estimate missing consumption. The [data contract](data-contract.md) defines accepted values and result fields.

Parsing holds the upload in memory. Analysis then loads up to 50,000 readings and produces at most 3,100 building/day rows, from the limit of 100 buildings and 31 days. Result endpoints return that result in one response. These are input limits, not measured throughput figures.

Datasets and successful results are immutable through the API. Deletion, retention policies, manual requeue of a failed job and an account management interface are not implemented. A new idempotency key creates a new analysis request when another run is needed.

A job has at most three attempts, including claims after an interruption. Repeated failures or interruptions can exhaust those attempts. Computation can occur more than once, while the lease checks restrict which worker may publish the result. See [restart behavior](architecture.md#what-happens-on-restart) for each stored job state.

No load testing, fault-injection campaign, penetration testing or high-availability assessment has been performed. The tests above establish specific calculation and database behaviors.

## Deployment and maintenance

The Compose setup binds the API to localhost and keeps PostgreSQL inside its network. No public deployment is included. Several operational concerns remain before exposing it publicly:

- Identity email confirmation and recovery delivery, rate limiting and external identity federation are not configured.
- HTTPS deployment and protected secret storage need configuration. Data Protection keys persist in a local volume but are not encrypted at rest. Backups are not configured.
- `/health/ready` checks database connectivity. There are no metrics or alerts for queue age or worker progress.
- Startup migrations suit the local single-instance setup. Multiple application instances need a coordinated schema migration step.
- Container images and GitHub Actions use major-version tags. NuGet dependencies are locked. Container and action digest pinning and automated dependency updates are not configured.

## Schema changes

The [design-time factory](../src/MeterFlow.Api/Data/DesignTimeDbContextFactory.cs) supplies EF with the model configuration for generating migrations. It does not require a live database or a checked-in password.

```sh
dotnet tool restore
dotnet ef migrations add MeaningfulName --project src/MeterFlow.Api --output-dir Data/Migrations
```

Review the generated schema changes and rerun the PostgreSQL tests. Existing migrations are committed, and Compose startup applies them automatically.
