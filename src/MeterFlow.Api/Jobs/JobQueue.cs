using MeterFlow.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace MeterFlow.Api.Jobs;
public sealed class JobQueue(AppDbContext db)
{
    public const int MaxAttempts = 3;
    public const int LeaseSeconds = 30;
    public async Task<AnalysisJob?> Claim(CancellationToken ct = default)
    {
        // An abandoned final attempt cannot remain running forever.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs" SET "Status" = 'failed', "ErrorCode" = 'attempts_exhausted',
                "FinishedAt" = clock_timestamp(), "LeaseToken" = NULL, "LeaseUntil" = NULL
            WHERE "Status" = 'running' AND "LeaseUntil" < clock_timestamp() AND "Attempts" >= {MaxAttempts}
            """, ct);
        var token = Guid.NewGuid();
        var jobs = await db.Jobs.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "Jobs"
                WHERE "Attempts" < {MaxAttempts} AND
                    (("Status" = 'queued' AND "AvailableAt" <= clock_timestamp()) OR
                     ("Status" = 'running' AND "LeaseUntil" < clock_timestamp()))
                ORDER BY "CreatedAt", "Id" FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE "Jobs" AS j SET "Status" = 'running', "Attempts" = j."Attempts" + 1,
                "LeaseToken" = {token}, "LeaseUntil" = clock_timestamp() + {LeaseSeconds} * interval '1 second',
                "StartedAt" = clock_timestamp(), "ErrorCode" = NULL
            FROM candidate WHERE j."Id" = candidate."Id" RETURNING j.*
            """).AsNoTracking().ToListAsync(ct);
        return jobs.SingleOrDefault();
    }
    public async Task<bool> Renew(AnalysisJob job, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs" SET "LeaseUntil" = clock_timestamp() + {LeaseSeconds} * interval '1 second'
            WHERE "Id" = {job.Id} AND "Status" = 'running' AND "LeaseToken" = {job.LeaseToken}
                AND "LeaseUntil" > clock_timestamp()
            """, ct) == 1;
    public async Task<bool> Complete(AnalysisJob job, string resultJson, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs" SET "ResultJson" = CAST({resultJson} AS jsonb), "Status" = 'succeeded',
                "FinishedAt" = clock_timestamp(), "LeaseToken" = NULL, "LeaseUntil" = NULL, "ErrorCode" = NULL
            WHERE "Id" = {job.Id} AND "Status" = 'running' AND "LeaseToken" = {job.LeaseToken}
                AND "LeaseUntil" > clock_timestamp()
            """, ct) == 1;
    public async Task<bool> Fail(AnalysisJob job, bool transient, CancellationToken ct = default)
    {
        var retry = transient && job.Attempts < MaxAttempts;
        var status = retry ? "queued" : "failed";
        var code = transient ? (retry ? "transient_retry" : "attempts_exhausted") : "processing_failed";
        var delay = Math.Min(30, 1 << job.Attempts);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs" SET "Status" = {status}, "ErrorCode" = {code},
                "AvailableAt" = clock_timestamp() + {delay} * interval '1 second',
                "FinishedAt" = CASE WHEN {retry} THEN NULL ELSE clock_timestamp() END,
                "LeaseToken" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = {job.Id} AND "Status" = 'running' AND "LeaseToken" = {job.LeaseToken}
                AND "LeaseUntil" > clock_timestamp()
            """, ct) == 1;
    }
    public static bool IsTransient(Exception error) => error is TimeoutException or NpgsqlException { IsTransient: true };
}
