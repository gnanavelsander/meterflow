# Tests, verification and limits

## Commands

Run from the repository root with .NET 10 and Docker running:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

[PostgresFixture](../tests/MeterFlow.Tests/PostgresFixture.cs) starts a real `postgres:17` container. Each integration test gets a separate database, applies the committed migrations, and drops the database on disposal. There is no EF in-memory or SQLite substitute for locking tests. To use a separately provisioned **test-only** PostgreSQL instance instead of Docker, set `METERFLOW_TEST_DB` to an Npgsql connection string whose role can create/drop test databases. Never point this at production. The test harness creates random `test_*` databases; it does not create missing users or roles.

Without Docker or PostgreSQL:

```sh
dotnet test -c Release --filter 'Category!=Integration'
```

This runs unit tests only and does not prove database behavior.

## Coverage map

| Test source | What it establishes |
| --- | --- |
| [CsvImportTests](../tests/MeterFlow.Tests/CsvImportTests.cs) | Strict headers, malformed rows, invalid numbers/times, negatives, duplicate readings, encoding, byte and row limits, preservation of zero |
| [AnalysisTests](../tests/MeterFlow.Tests/AnalysisTests.cs) | Midnight interval ownership, daily totals, zero vs missing, partial completeness and retry classification |
| [ApiTests](../tests/MeterFlow.Tests/ApiTests.cs) | Identity login, project ownership, pagination, health, OpenAPI and Problem Details |
| [DatasetApiTests](../tests/MeterFlow.Tests/DatasetApiTests.cs) | Owner-scoped imports/metadata and no dataset after invalid upload |
| [JobApiTests](../tests/MeterFlow.Tests/JobApiTests.cs) | Concurrent idempotency, conflict, validation before acceptance, job/result/export ownership, competing claims, stale-token fences, bounded retries, abandoned final attempt, hosted worker over a persisted queue |
| [scripts/demo.py](../scripts/demo.py) | Real HTTP registration-to-CSV workflow against the built Compose image, known sample answers and optional restart persistence |

The crash-recovery test deliberately expires a reserved job's lease and starts another claim against PostgreSQL. It checks the database state transition and stale-token fence; it does not simulate an OS-level process kill at every possible instruction. The hosted-worker test starts a new application instance over a previously queued job. The Compose demo additionally exercises an API restart and reads the previous result with the existing token.

[CI](../.github/workflows/ci.yml) runs build/test and a separate Docker Compose demonstration. It uploads TRX test reports and synthetic result files. Service logs are collected on failure; credentials and bearer tokens are not intentionally printed by the scripts. Its temporary database password is generated per run and is not committed.

## Verification recorded for this delivery

The implementation workstation ran the Release build and unit tests successfully. Its Docker daemon was unavailable, so PostgreSQL integration tests and the complete container demo were verified in **GitHub Actions**, not claimed as local runs. [The workflow history](https://github.com/gnanavelsander/meterflow/actions/workflows/ci.yml) and each pull request's checks provide the exact run/commit evidence. The final successful check set covers 26 tests plus the Compose demonstration and restart check.

## Known boundaries

- Input is already hourly interval energy in UTC. No local-time conversion, cumulative-meter conversion or estimation is performed.
- The entire accepted CSV is parsed in memory, then persisted; limits are intentional. Analysis loads up to 50,000 measurements and computes at most 3,100 building/day rows. Result endpoints return the bounded result in one response.
- Data and successful results are immutable through the API. There is no deletion, retention, failed-job manual requeue or account management UI. Use a new idempotency key for an intentional new job.
- Attempts are limited to three, including claims recovered after crashes. An extended outage or repeated interruptions can exhaust a job; no automatic infinite retries occur.
- Leases protect publication, not exactly-once computation. No performance/load, chaos, security penetration or high-availability qualification has been performed.
- Registration/login are suitable for a local demonstration. Email confirmation/recovery delivery, rate limiting, external identity federation, HTTPS deployment, protected key storage and backups are not configured.
- Health readiness tests database connectivity, not queue freshness. There are no metrics or alerts for stalled workers.
- Startup migrations are for local single-instance use. Coordinate schema changes separately before scaling application instances.
- Images and Actions use major-version tags; NuGet packages are locked. Full container/action digest pinning and automated dependency updates are future work.

## Developing schema changes

The [design-time factory](../src/MeterFlow.Api/Data/DesignTimeDbContextFactory.cs) lets EF generate migrations without a live database or a checked-in connection password.

```sh
dotnet tool restore
dotnet ef migrations add MeaningfulName --project src/MeterFlow.Api --output-dir Data/Migrations
```

Review generated SQL and model changes, then rerun PostgreSQL integration tests. Existing migrations are committed; ordinary Compose startup applies them automatically.
