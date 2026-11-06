#!/usr/bin/env python3
"""Exercise the running API using only Python's standard library and synthetic data."""
import argparse
import csv
import io
import json
from pathlib import Path
import secrets
import subprocess
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--verify-restart", action="store_true", help="Restart the local Compose API and verify the existing token and result")
    args = parser.parse_args()
    base = args.base_url.rstrip("/")
    token = None

    def call(method, path, body=None, expected=200, headers=None, raw=False):
        request_headers = dict(headers or {})
        if token:
            request_headers["Authorization"] = "Bearer " + token
        if body is not None and not isinstance(body, bytes):
            body = json.dumps(body).encode()
            request_headers["Content-Type"] = "application/json"
        req = urllib.request.Request(base + path, data=body, headers=request_headers, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read()
            if response.status != expected:
                raise RuntimeError(f"{method} {path}: expected {expected}, got {response.status}; request ID {response.headers.get('X-Request-ID')}")
            if raw:
                return content
            return json.loads(content) if content else None

    # Unique account per run. Credentials and bearer tokens remain in memory and are never printed.
    credentials = {"email": f"demo-{uuid.uuid4().hex}@example.test", "password": "Demo-" + secrets.token_urlsafe(24) + "1!"}
    call("POST", "/auth/register", credentials)
    token = call("POST", "/auth/login", credentials)["accessToken"]
    project = call("POST", "/api/projects/", {"name": "Synthetic campus", "description": "Known-answer UTC analysis"}, 201)
    print("Created project", project["id"])
    upload = f"/api/projects/{project['id']}/datasets?filename=hourly-energy.csv"
    call("POST", upload, (ROOT / "samples/invalid-negative.csv").read_bytes(), 422, {"Content-Type": "text/csv"})
    listing = call("GET", f"/api/projects/{project['id']}/datasets")
    assert listing["total"] == 0, "Invalid upload must not create a partial dataset"
    dataset = call("POST", upload, (ROOT / "samples/hourly-energy.csv").read_bytes(), 201, {"Content-Type": "text/csv"})
    assert dataset["rowCount"] == 26
    print("Imported dataset", dataset["id"], "(26 measurements)")
    settings = {"datasetId": dataset["id"], "startDate": "2026-01-01", "endDateExclusive": "2026-01-03"}
    key = {"Idempotency-Key": uuid.uuid4().hex}
    job = call("POST", "/api/jobs", settings, 202, key)
    assert call("POST", "/api/jobs", settings, 202, key)["id"] == job["id"]
    call("POST", "/api/jobs", dict(settings, endDateExclusive="2026-01-02"), 409, key)
    print("Queued job", job["id"], "(idempotency replay and conflict verified)")
    deadline = time.monotonic() + 60
    while True:
        status = call("GET", job["statusUrl"])
        if status["status"] == "succeeded":
            break
        if status["status"] == "failed":
            raise RuntimeError("Analysis failed: " + str(status["errorCode"]))
        if time.monotonic() > deadline:
            raise TimeoutError("Job did not finish in 60 seconds")
        time.sleep(0.25)
    rows = call("GET", job["resultUrl"])
    expected = json.loads((ROOT / "samples/expected-summary.json").read_text())
    actual = [{**{k: row[k] for k in ("buildingId", "day", "consumptionKwh", "measuredHours", "complete")},
               "missingCount": len(row["missingHourEnds"])} for row in rows]
    assert actual == expected, f"Unexpected analysis summary: {actual}"
    if args.verify_restart:
        subprocess.run(["docker", "compose", "restart", "api"], cwd=ROOT, check=True)
        restart_deadline = time.monotonic() + 60
        while True:
            try:
                call("GET", "/health/ready", raw=True)
                break
            except (OSError, RuntimeError):
                if time.monotonic() > restart_deadline:
                    raise TimeoutError("API did not become ready after restart")
                time.sleep(1)
        assert call("GET", job["resultUrl"]) == rows, "Persisted result or original bearer token failed after restart"
        print("PASS: existing bearer token and result survived API restart.")
    exported = call("GET", job["resultUrl"] + ".csv", raw=True)
    exported_rows = list(csv.DictReader(io.StringIO(exported.decode())))
    assert len(exported_rows) == 4
    assert exported_rows[0]["consumption_kwh"] in ("23", "23.000000")
    assert exported_rows[1]["consumption_kwh"] == "", "Missing consumption must export as an empty cell"
    output = ROOT / "artifacts"
    output.mkdir(exist_ok=True)
    (output / "demo-result.json").write_text(json.dumps(rows, indent=2) + "\n")
    (output / "demo-result.csv").write_bytes(exported)
    print("PASS: A = 23 kWh, B = 2.5 kWh on January 1; January 2 has missing data, not zero.")
    print("Saved JSON and CSV under", output)


if __name__ == "__main__":
    main()
