using System.Text.Json;
using MeterFlow.Api.Analysis;
using MeterFlow.Api.Data;
using Microsoft.EntityFrameworkCore;
namespace MeterFlow.Api.Jobs;
public sealed class JobProcessor(AppDbContext db, JobQueue queue)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<bool> Process(AnalysisJob job, CancellationToken ct)
    {
        if (job.AnalysisVersion != AnalysisCalculator.Version) throw new InvalidOperationException("Unsupported analysis version.");
        // Load every building, even if it has no readings in the selected interval. Import caps this at 50000 rows.
        var measurements = await db.Measurements.AsNoTracking().Where(x => x.DatasetId == job.DatasetId).ToListAsync(ct);
        if (measurements.Count == 0) throw new InvalidOperationException("The immutable dataset is empty.");
        var result = AnalysisCalculator.Calculate(measurements, job.StartDate, job.EndDateExclusive, ct);
        return await queue.Complete(job, JsonSerializer.Serialize(result, JsonOptions), ct);
    }
}
public sealed class AnalysisWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Worker:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
                var job = await queue.Claim(stoppingToken);
                if (job is null) { await Task.Delay(500, stoppingToken); continue; }
                using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["Attempt"] = job.Attempts });
                logger.LogInformation("Job {JobId} reserved for attempt {Attempt}", job.Id, job.Attempts);
                using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = Heartbeat(job, work);
                try
                {
                    var committed = await scope.ServiceProvider.GetRequiredService<JobProcessor>().Process(job, work.Token);
                    logger.LogInformation("Job {JobId} result committed: {Committed}", job.Id, committed);
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested)
                { logger.LogInformation("Job {JobId} interrupted; its lease allows recovery", job.Id); }
                catch (Exception error)
                {
                    logger.LogWarning("Job {JobId} failed with {ExceptionType}", job.Id, error.GetType().Name);
                    await queue.Fail(job, JobQueue.IsTransient(error), stoppingToken);
                }
                finally { await work.CancelAsync(); await heartbeat; }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Queue unavailable: {ExceptionType}", error.GetType().Name);
                try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task Heartbeat(AnalysisJob job, CancellationTokenSource work)
    {
        try
        {
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(JobQueue.LeaseSeconds / 3), work.Token);
                await using var scope = scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<JobQueue>().Renew(job, work.Token))
                { await work.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Lease renewal for {JobId} failed with {ExceptionType}", job.Id, error.GetType().Name);
            await work.CancelAsync();
        }
    }
}
