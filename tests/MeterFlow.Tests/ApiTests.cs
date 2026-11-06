using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeterFlow.Api.Projects;
namespace MeterFlow.Tests;
[Trait("Category", "Integration")]
public sealed partial class ApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiFactory factory = null!;
    private HttpClient client = null!;
    public async Task InitializeAsync() { factory = await ApiFactory.Create(postgres.ConnectionString); client = factory.CreateClient(); }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeDatabase(); }
    private async Task<HttpClient> Login()
    {
        var user = factory.CreateClient();
        var credentials = new { email = $"test-{Guid.NewGuid():N}@example.test", password = "Synthetic-Test-Password1!" };
        (await user.PostAsJsonAsync("/auth/register", credentials)).EnsureSuccessStatusCode();
        var response = await user.PostAsJsonAsync("/auth/login", credentials); response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        return user;
    }
    private static async Task<Guid> Project(HttpClient user)
    {
        var response = await user.PostAsJsonAsync("/api/projects/", new { name = "Synthetic campus", description = "test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnergyProject>())!.Id;
    }
    [Fact]
    public async Task Identity_and_project_ownership_are_enforced()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects/")).StatusCode);
        using var owner = await Login(); using var other = await Login();
        var id = await Project(owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/projects/{id}")).StatusCode);
        var list = await other.GetFromJsonAsync<JsonElement>("/api/projects/?page=1&pageSize=2");
        Assert.Equal(0, list.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/projects/?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync("/api/projects/", new { name = " " })).StatusCode);
    }
    [Fact]
    public async Task Health_openapi_and_problem_details_are_available()
    {
        (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
        var openapi = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.Equal("bearer", openapi.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        var submit = openapi.GetProperty("paths").GetProperty("/api/jobs").GetProperty("post");
        Assert.True(submit.GetProperty("responses").TryGetProperty("202", out _));
        Assert.Contains(submit.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(submit.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.Contains("Synthetic campus", await client.GetStringAsync("/openapi/v1.json"));
        var response = await client.GetAsync("/does-not-exist");
        Assert.True(response.Headers.Contains("X-Request-ID"));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
