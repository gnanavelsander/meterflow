using MeterFlow.Api.Analysis;
using MeterFlow.Api.Datasets;
using MeterFlow.Api.Jobs;
namespace MeterFlow.Tests;
public class AnalysisTests
{
    [Fact] public void Hour_end_midnight_belongs_to_previous_day_and_missing_is_not_zero()
    {
        var start = new DateOnly(2026, 1, 1);
        var midnight = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var data = Enumerable.Range(1, 24).Select(hour => new Measurement { BuildingId = "A", Timestamp = midnight.AddHours(hour), ConsumptionKwh = hour == 1 ? 0 : 1 }).ToList();
        data.Add(new() { BuildingId = "B", Timestamp = midnight.AddHours(1), ConsumptionKwh = 0 });
        data.Add(new() { BuildingId = "C", Timestamp = midnight.AddDays(-1), ConsumptionKwh = 9 });
        var rows = AnalysisCalculator.Calculate(data, start, start.AddDays(2));
        var a = Assert.Single(rows, x => x.BuildingId == "A" && x.Day == start);
        Assert.Equal(23m, a.ConsumptionKwh); Assert.True(a.Complete); Assert.Empty(a.MissingHourEnds);
        var b = Assert.Single(rows, x => x.BuildingId == "B" && x.Day == start);
        Assert.Equal(0m, b.ConsumptionKwh); Assert.Equal(1, b.MeasuredHours); Assert.Equal(23, b.MissingHourEnds.Length); Assert.False(b.Complete);
        var absent = Assert.Single(rows, x => x.BuildingId == "A" && x.Day == start.AddDays(1));
        Assert.Null(absent.ConsumptionKwh); Assert.Equal(24, absent.MissingHourEnds.Length);
        Assert.All(rows.Where(x => x.BuildingId == "C"), x => Assert.Null(x.ConsumptionKwh));
    }
    [Fact] public void Retry_classification_does_not_retry_permanent_errors()
    {
        Assert.True(JobQueue.IsTransient(new TimeoutException()));
        Assert.False(JobQueue.IsTransient(new InvalidOperationException()));
        Assert.False(JobQueue.IsTransient(new ArgumentException()));
    }
}
