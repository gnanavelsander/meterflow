using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeterFlow.Api.Analysis;
using MeterFlow.Api.Data;
using MeterFlow.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace MeterFlow.Api.Jobs;
public static class JobEndpoints
{
    public static void MapJobs(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs", async (CreateAnalysis request, HttpRequest http, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var key = http.Headers["Idempotency-Key"].ToString();
            if (key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not ':'))
                throw new ApiFault(400, "Invalid idempotency key", "Send Idempotency-Key: 1..128 ASCII letters/digits or - _ . :");
            if (request.StartDate < new DateOnly(1970, 1, 1) || request.EndDateExclusive > new DateOnly(9999, 1, 1)
                || request.EndDateExclusive <= request.StartDate || request.EndDateExclusive.DayNumber - request.StartDate.DayNumber > 31)
                throw new ApiFault(422, "Invalid analysis range", "Use 1..31 complete UTC days, from startDate inclusive to endDateExclusive exclusive (1970..9998).");
            if (!await db.Datasets.AnyAsync(x => x.Id == request.DatasetId && x.Project.OwnerId == owner, ct)) throw ApiFault.Missing();
            var canonical = FormattableString.Invariant($"{request.DatasetId:D}|{request.StartDate:yyyy-MM-dd}|{request.EndDateExclusive:yyyy-MM-dd}|{AnalysisCalculator.Version}");
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == owner && x.IdempotencyKey == key, ct);
            if (job is null)
            {
                job = new() { OwnerId = owner, DatasetId = request.DatasetId, StartDate = request.StartDate,
                    EndDateExclusive = request.EndDateExclusive, IdempotencyKey = key, RequestHash = hash };
                db.Jobs.Add(job);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    db.ChangeTracker.Clear();
                    job = await db.Jobs.AsNoTracking().SingleAsync(x => x.OwnerId == owner && x.IdempotencyKey == key, ct);
                }
            }
            if (job.RequestHash != hash) throw new ApiFault(409, "Idempotency conflict", "This key was already used with a different dataset, range or analysis version.");
            return Results.Accepted($"/api/jobs/{job.Id}", View(job));
        }).WithTags("Jobs").WithSummary("Queue daily UTC analysis; requires an Idempotency-Key header.");
        api.MapGet("/jobs", async (string? status, Guid? datasetId, int? page, int? pageSize, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (status is not null && status is not ("queued" or "running" or "succeeded" or "failed")) throw new ApiFault(400, "Invalid status", "Use queued, running, succeeded or failed.");
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var (p, size) = Paging.Validate(page, pageSize);
            var query = db.Jobs.AsNoTracking().Where(x => x.OwnerId == owner);
            if (status is not null) query = query.Where(x => x.Status == status);
            if (datasetId is not null) query = query.Where(x => x.DatasetId == datasetId);
            var total = await query.CountAsync(ct);
            // Project to a summary so list reads do not fetch potentially large result JSON.
            var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Skip((p - 1) * size).Take(size).Select(x => new { x.Id, x.DatasetId, x.Status, x.Attempts, x.CreatedAt, x.FinishedAt }).ToListAsync(ct);
            return Results.Ok(new { page = p, pageSize = size, total, items });
        }).WithTags("Jobs");
        api.MapGet("/jobs/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => Results.Ok(View(await Owned(id, user, db, ct))))
            .WithTags("Jobs");
        api.MapGet("/jobs/{id:guid}/result", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var job = await Owned(id, user, db, ct); RequireResult(job);
            return Results.Content(job.ResultJson!, "application/json");
        }).WithTags("Results");
        api.MapGet("/jobs/{id:guid}/result.csv", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var job = await Owned(id, user, db, ct); RequireResult(job);
            var rows = JsonSerializer.Deserialize<List<DailyResult>>(job.ResultJson!, JobProcessor.JsonOptions)!;
            var csv = new StringBuilder("building_id,day,consumption_kwh,measured_hours,expected_hours,complete,missing_hour_ends\r\n");
            foreach (var row in rows)
                csv.Append(row.BuildingId).Append(',').Append(row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.ConsumptionKwh?.ToString("0.######", CultureInfo.InvariantCulture) ?? "").Append(',')
                    .Append(row.MeasuredHours).Append(',').Append(row.ExpectedHours).Append(',').Append(row.Complete ? "true" : "false").Append(',')
                    .AppendJoin(';', row.MissingHourEnds.Select(x => x.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))).Append("\r\n");
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"analysis-{job.Id}.csv");
        }).WithTags("Results");
    }
    private static async Task<AnalysisJob> Owned(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct) ?? throw ApiFault.Missing();
    }
    private static void RequireResult(AnalysisJob job)
    {
        if (job.Status != "succeeded" || job.ResultJson is null) throw new ApiFault(409, "Result not available", "Results are available only after the job succeeds.");
    }
    private static object View(AnalysisJob j) => new { j.Id, j.DatasetId, j.StartDate, j.EndDateExclusive, j.AnalysisVersion,
        j.Status, j.Attempts, maxAttempts = JobQueue.MaxAttempts, j.CreatedAt, j.StartedAt, j.FinishedAt, j.ErrorCode,
        statusUrl = $"/api/jobs/{j.Id}", resultUrl = $"/api/jobs/{j.Id}/result" };
}
