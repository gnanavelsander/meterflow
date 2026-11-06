# CSV and analysis rules

## Hourly readings

Each CSV row records energy in kWh over the preceding hour. Its `timestamp` is the end of that hour. The input is interval energy, not a cumulative meter reading or a power value in kW.

```csv
building_id,timestamp,consumption_kwh
A,2026-01-01T01:00:00Z,0
A,2026-01-01T02:00:00Z,2.5
```

The first row records zero consumption from 00:00 to 01:00 UTC. The second records 2.5 kWh over the next hour. A reading at `2026-01-02T00:00:00Z` belongs to January 1 because its interval starts on that day. All analysis uses UTC, with no local-time or daylight-saving conversion.

## Validation

[CsvImport.ReadAsync and Parse](../src/MeterFlow.Api/Datasets/CsvImport.cs) apply the following rules before the handler writes anything to the database.

| Field or file property | Accepted values |
| --- | --- |
| Encoding and syntax | UTF-8, with an optional leading byte order mark (BOM). CsvHelper handles comma-separated fields and standard quoting. Blank records and quoted multiline fields are rejected. |
| Header | Exactly `building_id`, `timestamp`, `consumption_kwh`, in any order. Missing, duplicated or extra columns are rejected. Headers and fields are not trimmed. |
| `building_id` | 1 to 64 ASCII letters or digits, dots, underscores or hyphens. The first character must be a letter or digit. IDs are case-sensitive. |
| `timestamp` | Exactly `yyyy-MM-ddTHH:00:00Z`, after `1970-01-01T00:00:00Z`. Local times, numeric offsets and fractional hours are rejected. |
| `consumption_kwh` | 0 through 1,000,000,000 inclusive, using a decimal point and at most six decimal places. Signs, exponents and thousands separators are rejected. |
| Duplicate readings | A building and timestamp pair may appear once per dataset. |
| Size and count | At least one measurement, at most 5 MiB (5,242,880 bytes), 50,000 measurement rows and 100 distinct buildings. |

Consumption is stored as `numeric(18,6)` in PostgreSQL. The building ID character rules also keep exported identifiers from starting with spreadsheet formula characters.

## Upload and rejection behavior

Send the raw file with `Content-Type: text/csv` to `/api/projects/{projectId}/datasets?filename=hourly-energy.csv`. The endpoint expects the CSV itself as the body, not a multipart form. Supply a plain filename of 1 to 255 characters with no path separators or control characters. The filename is metadata and is never used as a storage path.

Invalid CSV returns HTTP 422 with Problem Details, a JSON error object. Its `errors` field contains entries such as `{"row": 2, "reason": "..."}`. The header is row 1. Header or structural errors can stop parsing immediately, while row validation reports up to 100 errors. An oversized body returns 413. A content type other than `text/csv` returns 415.

The entire import is rejected if validation fails. [DatasetEndpoints.MapDatasets](../src/MeterFlow.Api/Datasets/DatasetEndpoints.cs) calls `SaveChangesAsync` only after parsing succeeds, committing the dataset and all readings in one transaction. A composite primary key on `(DatasetId, BuildingId, Timestamp)` also prevents duplicate readings in the database.

Each accepted upload records its original filename, SHA-256 hash of the original bytes, UTC upload time and row count. Raw files are not retained. Uploading the same bytes again creates another dataset with the same hash, since hashes identify content but do not deduplicate imports. The API provides no endpoint for changing an accepted dataset.

## Daily analysis

`POST /api/jobs` accepts `datasetId`, `startDate` and `endDateExclusive`. The range must cover 1 to 31 complete UTC days. Start is inclusive and end is exclusive, with supported days from 1970 through 9998.

[AnalysisCalculator.Calculate](../src/MeterFlow.Api/Analysis/AnalysisCalculator.cs) expects hour-ending readings from 01:00 through 00:00 the following day. That gives 24 expected readings per day, or 48 in a two-day window. Every building found anywhere in the dataset appears on each selected day, including buildings that have no readings within that range.

| Result | Meaning |
| --- | --- |
| `consumptionKwh: 0`, `measuredHours > 0` | The recorded readings sum to zero. |
| `consumptionKwh: null`, `measuredHours: 0` | There are no readings for the day. Consumption is unknown. |
| Numeric consumption, `complete: false` | The value is the sum of available readings. It is not an estimate of the full day's consumption. |
| `missingHourEnds` | UTC end timestamps for the intervals with no reading. |
| `complete: true` | All 24 expected readings are present. |

CSV export leaves the consumption cell empty for `null`. Numbers have up to six decimal places, and missing hour-end timestamps share one field separated by semicolons. The calculator does not fill gaps or interpolate values.

The job stores its dataset ID, selected dates and analysis version (`daily-utc-v1`), along with the request hash, attempts and execution timestamps. `GET /api/datasets/{id}` returns the input hash and upload metadata. The [sample answers](../samples/expected-summary.json) cover a complete day, a partial day, recorded zeros and a day without readings.
