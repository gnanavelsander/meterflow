using Microsoft.AspNetCore.Mvc.Testing;

namespace MeterFlow.Tests;
public sealed class FoundationTests
{
    [Fact]
    public async Task Host_exposes_health_openapi_and_request_ids()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var health = await client.GetAsync("/health/live");
        health.EnsureSuccessStatusCode();
        Assert.True(health.Headers.Contains("X-Request-ID"));
        var schema = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("openapi", schema);
    }
}
