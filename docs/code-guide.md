# Code guide and interview preparation

Read one request from entry point to persisted result before exploring infrastructure details.

1. **[Program.cs](../src/MeterFlow.Api/Program.cs)** — service registration, bearer-only `/api` group, middleware order, migrations and hosted-worker startup. This is the composition root, not the business-logic layer.
2. **[ProjectEndpoints.MapProjects](../src/MeterFlow.Api/Projects/ProjectEndpoints.cs)** — start with a small route: validation, owner from claims, EF persistence, Created response and paginated search.
3. **[DatasetEndpoints.MapDatasets](../src/MeterFlow.Api/Datasets/DatasetEndpoints.cs)** and **[CsvImport.ReadAsync / Parse](../src/MeterFlow.Api/Datasets/CsvImport.cs)** — follow a byte-limited CSV upload into syntax/domain validation, hash calculation and one transactional save. No raw file path exists.
4. **[AppDbContext.OnModelCreating](../src/MeterFlow.Api/Data/AppDbContext.cs)** — inspect decimal precision, composite keys, foreign keys and the idempotency index. Compare this with the committed migrations.
5. **[JobEndpoints.MapJobs](../src/MeterFlow.Api/Jobs/JobEndpoints.cs)** — follow settings validation and concurrent idempotent creation. Notice that job acceptance and job success are distinct responses.
6. **[JobQueue.Claim / Renew / Complete / Fail](../src/MeterFlow.Api/Jobs/JobQueue.cs)** — read the SQL conditions carefully. The lease token and expiry checks are what make recovery safe.
7. **[AnalysisWorker.ExecuteAsync / Heartbeat and JobProcessor.Process](../src/MeterFlow.Api/Jobs/JobProcessor.cs)** — follow polling, scoped EF contexts, cancellation, retry classification and result publication. The heartbeat uses its own context because EF contexts cannot execute concurrent operations safely.
8. **[AnalysisCalculator.Calculate](../src/MeterFlow.Api/Analysis/AnalysisCalculator.cs)** — the calculation is independent of HTTP and persistence. A dictionary looks up each expected hour-end for each building/day. Import and date-range limits bound memory and computation.
9. **[JobApiTests](../tests/MeterFlow.Tests/JobApiTests.cs)** — read the competing-claim, stale-worker and restarted-host tests as executable descriptions of the reliability contract.

Other useful entry points: [error format](../src/MeterFlow.Api/Infrastructure/ApiFault.cs), [database health](../src/MeterFlow.Api/Infrastructure/DatabaseHealth.cs), [OpenAPI examples](../src/MeterFlow.Api/Infrastructure/OpenApiGuide.cs), [test database lifecycle](../tests/MeterFlow.Tests/PostgresFixture.cs), [complete demo](../scripts/demo.py).

## Questions to practise

- Why does a reading at midnight belong to the previous day? Show the `hour = 1..24` loop and a test that would fail if it belonged to the next day.
- Why is a partial daily sum not a full-day estimate? How can a consumer distinguish measured zero from no readings?
- What prevents a user from supplying another user's project or dataset ID? Point to each ownership predicate, including result export.
- What happens if two requests reuse an idempotency key at the same instant? Why is an application-level lookup insufficient without the unique index?
- Why return 202 instead of 200 for a new job? What guarantees exist at that point?
- What does `SKIP LOCKED` do when two workers compete? How does the lease differ from a database transaction held throughout processing?
- What happens if the worker crashes before computing, during result publication, or after the commit but before it sees the response?
- Why does a stale worker's token matter? Explain at-least-once computation without claiming exactly-once execution.
- Why is an EF in-memory provider insufficient evidence for PostgreSQL locks and concurrent inserts?
- Why retain the analysis version and immutable dataset identity? What would need to change if users could edit imported measurements?
- Which limits make loading a whole dataset acceptable here? What would need to change for millions of rows?
- What is deliberately absent before public deployment? Discuss email delivery, token-key protection, HTTPS, migration coordination, backups, rate limits, retention and worker monitoring.

These questions describe implementation tradeoffs visible in the code. They do not imply a history of comparing or benchmarking alternative technologies.
