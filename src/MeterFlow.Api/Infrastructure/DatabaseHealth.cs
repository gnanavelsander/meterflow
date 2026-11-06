using MeterFlow.Api.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
namespace MeterFlow.Api.Infrastructure;
public sealed class DatabaseHealth(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable");
    }
}
