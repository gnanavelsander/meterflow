using MeterFlow.Api.Data;
using MeterFlow.Api.Infrastructure;
using MeterFlow.Api.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>((services, options) => options.UseNpgsql(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database must be configured.")));
builder.Services.AddIdentityApiEndpoints<IdentityUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12;
    options.Lockout.MaxFailedAccessAttempts = 5;
}).AddEntityFrameworkStores<AppDbContext>();
builder.Services.AddAuthorization();
builder.Services.AddDataProtection().SetApplicationName("MeterFlow").PersistKeysToFileSystem(
    new DirectoryInfo(builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".keys")));
builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("postgres", tags: ["ready"]);
builder.Logging.AddJsonConsole();
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = context.TraceIdentifier });
    await next(context);
    app.Logger.LogInformation("HTTP {Method} {Path} returned {StatusCode}",
        context.Request.Method, context.Request.Path, context.Response.StatusCode);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGroup("/auth").WithTags("Identity").MapIdentityApi<IdentityUser>();
var api = app.MapGroup("/api").RequireAuthorization(new AuthorizationPolicyBuilder(IdentityConstants.BearerScheme)
    .RequireAuthenticatedUser().Build());
api.MapProjects();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}
app.Run();
public partial class Program;
