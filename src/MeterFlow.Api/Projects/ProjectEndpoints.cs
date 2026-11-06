using System.Security.Claims;
using MeterFlow.Api.Data;
using MeterFlow.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace MeterFlow.Api.Projects;
public sealed record CreateProject(string? Name, string? Description);
public static class ProjectEndpoints
{
    public static void MapProjects(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/projects").WithTags("Projects");
        group.MapPost("/", async (CreateProject input, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var name = input.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100 || input.Description?.Length > 1000)
                throw new ApiFault(422, "Invalid project", "name must contain 1..100 characters; description can contain at most 1000.");
            var project = new EnergyProject { Name = name, Description = input.Description ?? "", OwnerId = user.FindFirstValue(ClaimTypes.NameIdentifier)! };
            db.Projects.Add(project); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/projects/{project.Id}", project);
        }).WithSummary("Create a project owned by the authenticated user");
        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, string? search, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, s) = Paging.Validate(page, pageSize);
            if (search?.Length > 100) throw new ApiFault(400, "Invalid search", "search must be at most 100 characters.");
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var query = db.Projects.AsNoTracking().Where(x => x.OwnerId == owner);
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.ToLower().Contains(search.ToLower()));
            return Results.Ok(new { page = p, pageSize = s, total = await query.CountAsync(ct),
                items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((p-1)*s).Take(s).ToListAsync(ct) });
        }).WithSummary("List your projects, with optional name search and pagination");
        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            return await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
                is { } project ? Results.Ok(project) : throw ApiFault.Missing();
        }).WithSummary("Get one of your projects");
    }
}
