# Running the API and trying requests

## Start the local stack

Follow the [README startup commands](../README.md#run-locally-optional) from a new checkout. Docker Compose starts PostgreSQL and the API. Check readiness before sending requests:

```sh
curl --fail http://localhost:8080/health/ready
```

If the API is still starting, wait and repeat the command. Use `docker compose logs --tail=80 api` to inspect startup. [Scalar](http://localhost:8080/scalar/v1) displays the endpoints and their request examples, and the underlying document is available as [OpenAPI JSON](http://localhost:8080/openapi/v1.json).

## Run the sample workflow

From the repository root:

```sh
python3 scripts/demo.py
```

The script uses Python's standard library and creates a synthetic account and project. It first submits an invalid CSV and checks that no dataset was saved. It then imports the 26 readings in [hourly-energy.csv](../samples/hourly-energy.csv), submits an analysis, checks repeated and conflicting idempotency keys, and polls for the result. The output is compared with [expected-summary.json](../samples/expected-summary.json) before JSON and CSV files are saved under the ignored `artifacts/` directory.

Each run creates new resources. Credentials and tokens stay in memory and are not printed. The workflow needs no external service or paid account.

To also check persistence across an API restart, run:

```sh
python3 scripts/demo.py --verify-restart
```

After obtaining a successful result, this option invokes `docker compose restart api`, waits for readiness and retrieves the same result with the existing token. It checks a completed job and token persistence. Recovery of an interrupted job is covered separately by the [PostgreSQL tests](testing.md#what-the-tests-check).

## Send the requests yourself

[examples/meterflow.http](../examples/meterflow.http) contains the sequence for an HTTP-file client. Response variables carry IDs and the access token from one request to the next. Its password is synthetic and intended for local use. Change the example email if it is already registered in your database.

In Scalar, follow these steps:

1. Call `POST /auth/register` with a synthetic email and a password of at least 12 characters. Identity requires uppercase and lowercase letters, a digit and a symbol.
2. Call `POST /auth/login` with those credentials and leave `useCookies` unset. Copy the returned `accessToken` into Scalar's Bearer authentication field. This is an opaque Identity token, not a JWT. `/auth/refresh` accepts the returned refresh token when needed.
3. Call `POST /api/projects/` with `{"name":"Synthetic campus","description":"Hourly energy quality checks"}` and copy its `id`.
4. Call `POST /api/projects/{projectId}/datasets?filename=hourly-energy.csv` with [the sample CSV](../samples/hourly-energy.csv) as a raw `text/csv` body. Keep the returned dataset ID.
5. Call `POST /api/jobs` with the body below and an `Idempotency-Key` such as `synthetic-analysis-1`. Replace the dataset placeholder. The 202 response includes the job `id`, `statusUrl` and `resultUrl`. The Location header also points to the status endpoint.
6. Poll `statusUrl` until the job is `succeeded` or `failed`. After success, fetch JSON from `resultUrl` or download CSV by appending `.csv` to that URL.

```json
{
  "datasetId": "<dataset UUID from the upload>",
  "startDate": "2026-01-01",
  "endDateExclusive": "2026-01-03"
}
```

Expected result:

| Building | UTC day | kWh | Measured hours | Missing hours |
| --- | --- | ---: | ---: | ---: |
| A | 2026-01-01 | 23 | 24 | 0 |
| A | 2026-01-02 | null | 0 | 24 |
| B | 2026-01-01 | 2.5 | 2 | 22 |
| B | 2026-01-02 | null | 0 | 24 |

Both buildings have a recorded zero in their first reading. A's reading at midnight on January 2 contributes to January 1, giving that day 24 readings. B has only two readings on January 1, so its 2.5 kWh is a partial sum. January 2 has no readings for either building and returns `null`. See the [data contract](data-contract.md#daily-analysis) for the exact interval rules.

## Endpoint reference

All `/api` routes require `Authorization: Bearer <accessToken>`.

| Method and route | Response |
| --- | --- |
| POST `/api/projects/` | Creates a project for the current user. Returns 201 and a Location header. |
| GET `/api/projects/?search=campus&page=1&pageSize=20` | Searches the current user's projects by name. |
| GET `/api/projects/{id}` | Returns a project owned by the current user. |
| POST `/api/projects/{id}/datasets?filename=...` | Imports the entire CSV or rejects it. An accepted upload returns 201. |
| GET `/api/projects/{id}/datasets?page=1&pageSize=20` | Lists dataset metadata for the project. |
| GET `/api/datasets/{id}` | Returns filename, SHA-256, upload time and row count. |
| POST `/api/jobs` | Saves or reuses an analysis job and returns 202. Requires an Idempotency-Key. |
| GET `/api/jobs?status=queued&datasetId=...&page=1&pageSize=20` | Lists the current user's jobs with optional filters. |
| GET `/api/jobs/{id}` | Returns status, attempts, settings and timestamps. |
| GET `/api/jobs/{id}/result` | Returns daily JSON after success. |
| GET `/api/jobs/{id}/result.csv` | Downloads the result as CSV after success. |
| GET `/health/live`, `/health/ready` | Checks API availability or database connectivity without authentication. |

List responses contain `page`, `pageSize`, `total` and `items`. Page defaults to 1 and size to 20. Accepted ranges are page 1 to 10,000 and size 1 to 100. Lists sort by timestamp with a UUID as a tie-breaker. Because pagination uses offsets, new inserts between requests can shift items between pages.

## Try the error cases

| Request | Expected response |
| --- | --- |
| Upload [invalid-negative.csv](../samples/invalid-negative.csv) | 422 with a reason for row 2. No dataset is saved. |
| Repeat a job's key with the same settings | 202 with the existing job ID. |
| Change the date range while reusing that key | 409 for conflicting settings. |
| Fetch a result before the job succeeds | 409 because no result is available yet. |
| Fetch a resource owned by another account | 404. |
| Fetch an `/api` resource without a valid token | 401. |

Application validation errors use Problem Details with `requestId`. CSV errors add row numbers in `errors`. Identity registration and password failures can use its ValidationProblem error dictionary. The `X-Request-ID` response header identifies the request in logs.

## Stop, restart and reset

```sh
docker compose down
```

This stops the stack and retains the database and token-key volumes. To start it again and run the restart check:

```sh
docker compose up -d
python3 scripts/demo.py --verify-restart
```

You can restart only the API with `docker compose restart api`. Stored jobs, results and token keys remain in the volumes.

`docker compose down --volumes` deletes all local users, imports, jobs and keys. Keep the password in `.env` unchanged when reusing a database volume, since the PostgreSQL image sets that password only when initializing an empty volume.
