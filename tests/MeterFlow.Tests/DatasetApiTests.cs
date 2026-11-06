using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
namespace MeterFlow.Tests;
public sealed partial class ApiTests
{
    private static Task<HttpResponseMessage> Upload(HttpClient user, Guid project, string csv = CsvImportTests.Good) =>
        user.PostAsync($"/api/projects/{project}/datasets?filename=synthetic.csv", new StringContent(csv, Encoding.UTF8, "text/csv"));
    private static async Task<Guid> Dataset(HttpClient user)
    {
        var response = await Upload(user, await Project(user)); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    [Fact] public async Task Imports_are_atomic_and_owner_scoped()
    {
        using var owner = await Login(); using var other = await Login();
        var project = await Project(owner);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(other, project)).StatusCode);
        var bad = await Upload(owner, project, CsvImportTests.Good + "A,invalid,-1\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/datasets")).GetProperty("total").GetInt32());
        var good = await Upload(owner, project); Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var data = await good.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, data.GetProperty("rowCount").GetInt32());
        Assert.Equal(64, data.GetProperty("sha256").GetString()!.Length);
        var id = data.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/datasets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/projects/{project}/datasets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/datasets/{id}")).StatusCode);
    }
}
