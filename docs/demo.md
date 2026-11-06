# Demonstration and API reference

## Start and inspect

Follow [README startup](../README.md). Use `curl --fail http://localhost:8080/health/ready` until ready; inspect startup with `docker compose logs --tail=80 api`. Browse [Scalar](http://localhost:8080/scalar/v1) and [OpenAPI JSON](http://localhost:8080/openapi/v1.json).

The fastest complete workflow is:

```sh
python3 scripts/demo.py
# Additionally verify the same token and results after restarting the API:
python3 scripts/demo.py --verify-restart
```

Python 3 uses only the standard library. The optional restart check invokes `docker compose` from the repository root. No account signup on an external service is involved. Credentials/tokens stay in memory and are not printed; each run creates a new synthetic account. Generated result files under `artifacts/` contain synthetic data only.

## Walk through requests yourself

[examples/meterflow.http](../examples/meterflow.http) contains the complete sequence for an HTTP-file client, with response variables linking requests. Its example password is intentionally synthetic and only for a local demo. Use a new example email if you repeat registration against existing data.

In Scalar, execute the same sequence:

1. `POST /auth/register`: supply a synthetic email and a password of at least 12 characters satisfying Identity's default uppercase/lowercase/digit/symbol requirements.
2. `POST /auth/login`: use those credentials, leaving `useCookies` unset. Copy `accessToken` into Scalar's Bearer authentication field. Access tokens are opaque; do not decode them as JWTs. `/auth/refresh` accepts the returned refresh token when needed.
3. `POST /api/projects/`: create `{"name":"Synthetic campus","description":"Hourly energy quality checks"}` and copy its `id`.
4. `POST /api/projects/{projectId}/datasets?filename=hourly-energy.csv`: paste [the sample CSV](../samples/hourly-energy.csv) as the raw `text/csv` body. The returned `id` is the dataset ID.
5. `POST /api/jobs`: supply the body below and an `Idempotency-Key`, such as `synthetic-analysis-1`. Replace the dataset placeholder. The 202 response includes `id`, `statusUrl`, `resultUrl`; the Location header also identifies the status resource.
6. Poll `statusUrl` until `succeeded` or `failed`. Fetch JSON from `resultUrl` and CSV from the same URL with `.csv` appended.

```json
{
  "datasetId": "<dataset UUID from the upload>",
  "startDate": "2026-01-01",
  "endDateExclusive": "2026-01-03"
}
```

Expected summary:

| Building | UTC day | kWh | Measured hours | Missing hours |
| --- | --- | ---: | ---: | ---: |
| A | 2026-01-01 | 23 | 24 | 0 |
| A | 2026-01-02 | null | 0 | 24 |
| B | 2026-01-01 | 2.5 | 2 | 22 |
| B | 2026-01-02 | null | 0 | 24 |

A's first reading and B's first reading are real zeros. A's midnight reading is included in January 1. January 2 has no readings, so it must not display zero. B's 2.5 kWh is a **partial** sum. See the [data contract](data-contract.md).

## Endpoints and error cases

All `/api` routes require `Authorization: Bearer <accessToken>`.

| Method / route | Purpose / response |
| --- | --- |
| POST `/api/projects/` | Create owned project; 201 + Location |
| GET `/api/projects/?search=campus&page=1&pageSize=20` | Search owned project names |
| GET `/api/projects/{id}` | Owned project |
| POST `/api/projects/{id}/datasets?filename=...` | All-or-nothing raw CSV import; 201 |
| GET `/api/projects/{id}/datasets?page=1&pageSize=20` | Dataset metadata list |
| GET `/api/datasets/{id}` | Filename, SHA-256, timestamp and row count |
| POST `/api/jobs` | Queue/reuse analysis; 202, required Idempotency-Key |
| GET `/api/jobs?status=queued&datasetId=...&page=1&pageSize=20` | Filter owned jobs |
| GET `/api/jobs/{id}` | Status, attempts, settings and timestamps |
| GET `/api/jobs/{id}/result` | Daily JSON after success |
| GET `/api/jobs/{id}/result.csv` | Download after success |
| GET `/health/live`, `/health/ready` | Process / database checks; no auth |

List responses contain `page`, `pageSize`, `total`, `items`. Page defaults to 1 and size to 20; valid ranges are page 1–10,000, size 1–100. Ordering uses a timestamp and UUID tie-breaker; inserts between offset-based pages can shift items.

Try [invalid-negative.csv](../samples/invalid-negative.csv): expect 422 with row 2 and a reason, and no new dataset. Replay a job with the same key and settings: expect the existing job. Change the range with the same key: expect 409. Request a result before success: expect 409. Use a second account's token for any resource: expect 404. No token: expect 401.

Validation errors use Problem Details with `requestId`; CSV errors additionally contain `errors` with row numbers. Identity may return its framework ValidationProblem error dictionary for registration/password failures. `X-Request-ID` is present for correlating requests with logs.

## Stop, restart and reset

`docker compose restart api` retains queued jobs, results and token keys. `docker compose down` stops the stack while retaining volumes. To intentionally delete **all local users, imports, jobs and keys**, run `docker compose down --volumes`. Do not change the database password in `.env` without also handling the existing database volume; the PostgreSQL image only initializes its password on first creation.
