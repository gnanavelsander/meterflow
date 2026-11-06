using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeterFlow.Api.Data;
using MeterFlow.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace MeterFlow.Tests;
public sealed partial class ApiTests
{
    private static async Task<HttpResponseMessage> Submit(HttpClient user, Guid dataset, string key = "test-key", string end = "2026-01-02")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        { Content = JsonContent.Create(new { datasetId = dataset, startDate = "2026-01-01", endDateExclusive = end }) };
        request.Headers.Add("Idempotency-Key", key);
        return await user.SendAsync(request);
    }
    private async Task<T> Queue<T>(Func<JobQueue, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JobQueue>());
    }
    private async Task ExecuteSql(string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql);
    }
    [Fact] public async Task Idempotency_is_atomic_and_payload_conflicts_are_explicit()
    {
        using var user = await Login(); var dataset = await Dataset(user);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Submit(user, dataset)));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(); ids.Add(body.GetProperty("id").GetGuid());
            Assert.Equal($"/api/jobs/{ids[^1]}", response.Headers.Location!.ToString());
        }
        Assert.Single(ids.Distinct());
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(user, dataset, end: "2026-01-03")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Submit(user, dataset, "bad-range", "2026-03-01")).StatusCode);
        Assert.Equal(1, (await user.GetFromJsonAsync<JsonElement>("/api/jobs")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await user.GetAsync($"/api/jobs/{ids[0]}/result")).StatusCode);
    }
    [Fact] public async Task Status_results_and_exports_are_owner_scoped()
    {
        using var owner = await Login(); using var other = await Login();
        var dataset = await Dataset(owner);
        Assert.Equal(HttpStatusCode.NotFound, (await Submit(other, dataset)).StatusCode);
        var response = await Submit(owner, dataset);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var job = (await Queue(q => q.Claim()))!;
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<JobProcessor>().Process(job, default));
        foreach (var suffix in new[] { "", "/result", "/result.csv" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/jobs/{id}{suffix}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/jobs/{id}{suffix}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/jobs/{id}{suffix}")).StatusCode);
        }
        var rows = await owner.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}/result");
        Assert.Equal(2.5m, rows[0].GetProperty("consumptionKwh").GetDecimal());
        Assert.Equal(22, rows[0].GetProperty("missingHourEnds").GetArrayLength());
        Assert.Contains("A,2026-01-01,2.5,2,24,false", await owner.GetStringAsync($"/api/jobs/{id}/result.csv"));
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/jobs")).GetProperty("total").GetInt32());
        var repeated = await Submit(owner, dataset);
        Assert.Equal(id, (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }
    [Fact] public async Task Concurrent_claims_and_crash_recovery_fence_stale_workers()
    {
        using var user = await Login(); await Submit(user, await Dataset(user));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Queue(q => q.Claim())));
        var first = Assert.Single(claims, x => x is not null)!;
        Assert.True(await Queue(q => q.Renew(first)));
        await ExecuteSql("UPDATE \"Jobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second'");
        var second = (await Queue(q => q.Claim()))!;
        Assert.Equal(first.Id, second.Id); Assert.Equal(2, second.Attempts); Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.False(await Queue(q => q.Renew(first)));
        Assert.False(await Queue(q => q.Complete(first, "[]")));
        Assert.False(await Queue(q => q.Fail(first, false)));
        Assert.True(await Queue(q => q.Complete(second, "[]")));
        Assert.False(await Queue(q => q.Complete(second, "[]")));
        Assert.Null(await Queue(q => q.Claim()));
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.ToListAsync();
        Assert.Single(jobs); Assert.Equal("succeeded", jobs[0].Status); Assert.NotNull(jobs[0].ResultJson);
    }
    [Fact] public async Task Transient_retries_are_bounded_and_permanent_failure_stops_immediately()
    {
        using var user = await Login(); var dataset = await Dataset(user);
        await Submit(user, dataset);
        for (var attempt = 1; attempt <= JobQueue.MaxAttempts; attempt++)
        {
            var job = (await Queue(q => q.Claim()))!; Assert.Equal(attempt, job.Attempts);
            Assert.True(await Queue(q => q.Fail(job, true)));
            Assert.Null(await Queue(q => q.Claim())); // Retry delay is respected.
            await ExecuteSql("UPDATE \"Jobs\" SET \"AvailableAt\" = clock_timestamp() - interval '1 second'");
        }
        Assert.Null(await Queue(q => q.Claim()));
        await Submit(user, dataset, "permanent");
        var permanent = (await Queue(q => q.Claim()))!;
        Assert.True(await Queue(q => q.Fail(permanent, false))); Assert.Null(await Queue(q => q.Claim()));
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.ToListAsync();
        Assert.All(jobs, j => Assert.Equal("failed", j.Status));
        Assert.Equal(1, jobs.Single(j => j.Id == permanent.Id).Attempts);
    }
    [Fact] public async Task Abandoned_final_attempt_is_failed_instead_of_stuck()
    {
        using var user = await Login(); await Submit(user, await Dataset(user));
        for (var attempt = 1; attempt <= JobQueue.MaxAttempts; attempt++)
        {
            Assert.NotNull(await Queue(q => q.Claim()));
            await ExecuteSql("UPDATE \"Jobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second'");
        }
        Assert.Null(await Queue(q => q.Claim()));
        var list = await user.GetFromJsonAsync<JsonElement>("/api/jobs?status=failed");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }
    [Fact] public async Task Hosted_worker_processes_a_previously_queued_job()
    {
        using var user = await Login(); var response = await Submit(user, await Dataset(user));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // Start a new application instance over the same persisted database, with its real hosted worker.
        var keys = Path.Combine(Path.GetTempPath(), "worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var worker = new ApiFactory(factory.ConnectionString, keys, true);
            using var workerClient = worker.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var status = await user.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", timeout.Token);
                if (status.GetProperty("status").GetString() == "succeeded") break;
                Assert.NotEqual("failed", status.GetProperty("status").GetString());
                await Task.Delay(100, timeout.Token);
            }
        }
        finally { if (Directory.Exists(keys)) Directory.Delete(keys, true); }
    }
}
