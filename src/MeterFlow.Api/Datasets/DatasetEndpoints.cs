using System.Security.Claims;
using MeterFlow.Api.Data;
using MeterFlow.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace MeterFlow.Api.Datasets;
public static class DatasetEndpoints
{
    public static void MapDatasets(this RouteGroupBuilder api)
    {
        api.MapPost("/projects/{projectId:guid}/datasets", async (Guid projectId, string filename, HttpRequest request,
            ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.OwnerId == owner, ct)) throw ApiFault.Missing();
            if (filename.Length is < 1 or > 255 || filename.Any(char.IsControl) || filename.IndexOfAny(['/', '\\']) >= 0)
                throw new ApiFault(422, "Invalid filename", "Supply a plain filename of 1..255 characters without path separators or control characters.");
            if (request.ContentType?.Split(';')[0].Trim() != "text/csv")
                throw new ApiFault(415, "Unsupported media type", "Send the file body as text/csv, not multipart form data.");
            if (request.ContentLength > CsvImport.MaxBytes) throw new ApiFault(413, "CSV too large", "Maximum upload size is 5 MiB.");
            var parsed = await CsvImport.ReadAsync(request.Body, ct);
            var dataset = new EnergyDataset { ProjectId = projectId, FileName = filename, Sha256 = parsed.Sha256, RowCount = parsed.Measurements.Count };
            db.Datasets.Add(dataset);
            foreach (var item in parsed.Measurements) item.DatasetId = dataset.Id;
            db.Measurements.AddRange(parsed.Measurements);
            await db.SaveChangesAsync(ct); // EF commits metadata and every measurement in one transaction.
            return Results.Created($"/api/datasets/{dataset.Id}", View(dataset));
        }).Accepts<string>("text/csv").WithTags("Datasets").WithSummary("Import an immutable, fully validated hourly CSV (maximum 5 MiB).");
        api.MapGet("/projects/{projectId:guid}/datasets", async (Guid projectId, int? page, int? pageSize, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.OwnerId == owner, ct)) throw ApiFault.Missing();
            var (p, size) = Paging.Validate(page, pageSize);
            var query = db.Datasets.AsNoTracking().Where(x => x.ProjectId == projectId);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.UploadedAt).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new { page = p, pageSize = size, total, items = items.Select(View) });
        }).WithTags("Datasets");
        api.MapGet("/datasets/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var dataset = await db.Datasets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Project.OwnerId == owner, ct) ?? throw ApiFault.Missing();
            return Results.Ok(View(dataset));
        }).WithTags("Datasets");
    }
    private static object View(EnergyDataset d) => new { d.Id, d.ProjectId, d.FileName, d.Sha256, d.UploadedAt, d.RowCount };
}
