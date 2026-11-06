# Input and analysis contract

## Hourly CSV

A row is energy in **kWh over the preceding hour**, with `timestamp` as the interval's end. Values are interval energy, not cumulative meter readings or power in kW. All dates and hours are UTC; daylight-saving time and local calendars are outside this version.

```csv
building_id,timestamp,consumption_kwh
A,2026-01-01T01:00:00Z,0
A,2026-01-01T02:00:00Z,2.5
```

The first row measures 00:00–01:00 and records genuine zero consumption. A reading at `2026-01-02T00:00:00Z` belongs to January 1.

[CsvImport.ReadAsync / Parse](../src/MeterFlow.Api/Datasets/CsvImport.cs) enforce:

- UTF-8, with an optional leading BOM. Standard comma-separated CSV quoting is handled by CsvHelper; quoted multiline fields are rejected. Blank records are invalid.
- Exactly `building_id`, `timestamp`, `consumption_kwh`, in any column order. No missing, duplicated or extra columns; no implicit header/field whitespace normalization.
- Building IDs: 1–64 ASCII letters/digits, dots, underscores or hyphens, starting with a letter/digit. IDs are case-sensitive. This also prevents spreadsheet formulas in exported identifiers.
- Timestamps: exactly `yyyy-MM-ddTHH:00:00Z`, after `1970-01-01T00:00:00Z`. Offsets, local time and fractional hours are rejected.
- Consumption: decimal point, no exponent, sign or thousands separator; 0 through 1,000,000,000 inclusive, at most six decimal places. EF stores `numeric(18,6)` to preserve decimal arithmetic.
- No duplicate `(building_id, timestamp)` within a dataset. Different uploads are separate immutable datasets; hashes identify identical bytes but do not deduplicate uploads.
- At least one measurement; at most 5 MiB (5,242,880 bytes), 50,000 measurement records and 100 distinct buildings.

Upload the raw file with `Content-Type: text/csv` to `/api/projects/{projectId}/datasets?filename=hourly-energy.csv`. This endpoint does not use multipart forms. Filenames must be plain names of 1–255 characters, without slashes or control characters. The API never uses them as storage paths.

Invalid content returns **422 Problem Details** with `errors: [{"row": 2, "reason": "..."}]`. Row numbering includes the header as row 1. Structural/header errors can stop parsing immediately; otherwise up to 100 errors are returned. An oversized body returns 413, and a wrong media type returns 415. No dataset or measurement is saved on validation failure.

[DatasetEndpoints](../src/MeterFlow.Api/Datasets/DatasetEndpoints.cs) validates in bounded memory, then saves metadata and all measurements in one EF transaction. Metadata records the original filename, SHA-256 of the original bytes, UTC upload time and row count. Raw files are **not retained**. Measurements use a composite primary key `(DatasetId, BuildingId, Timestamp)` as an additional uniqueness guarantee.

## Daily analysis

`POST /api/jobs` accepts `datasetId`, `startDate` and `endDateExclusive`. Select 1–31 complete UTC days; start is inclusive and end is exclusive. Supported days run from 1970 through 9998. Every building present anywhere in the dataset appears for every selected day, even when it has no readings inside the range.

[AnalysisCalculator.Calculate](../src/MeterFlow.Api/Analysis/AnalysisCalculator.cs) expects 24 hour-ending readings per day: 01:00 through 00:00 the next day. Thus a two-day window contains 48 expected intervals per building.

| Result | Meaning |
| --- | --- |
| `consumptionKwh: 0`, `measuredHours > 0` | Recorded readings sum to zero |
| `consumptionKwh: null`, `measuredHours: 0` | No readings; energy consumption is unknown |
| Numeric consumption, `complete: false` | Sum of available readings only, **not an estimate of the full day** |
| `missingHourEnds` | UTC end timestamps of all missing intervals |
| `complete: true` | All 24 expected readings exist |

Result CSV uses an empty consumption cell for `null`. It writes decimal values with up to six decimal places and separates missing hour-end timestamps with semicolons. No imputation or interpolation is performed.

Each job persists the immutable dataset ID, selected dates, request hash, analysis version (`daily-utc-v1`), attempts and timestamps. The dataset's SHA-256 and metadata are available from `GET /api/datasets/{id}`. [Known sample answers](../samples/expected-summary.json) exercise a complete day, a partial day, zeros and an entirely missing day.
