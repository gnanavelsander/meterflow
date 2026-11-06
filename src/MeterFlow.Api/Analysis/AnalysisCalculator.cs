using MeterFlow.Api.Datasets;
namespace MeterFlow.Api.Analysis;
public sealed record DailyResult(string BuildingId, DateOnly Day, decimal? ConsumptionKwh,
    int MeasuredHours, int ExpectedHours, DateTime[] MissingHourEnds)
{
    public bool Complete => MeasuredHours == ExpectedHours;
}
public static class AnalysisCalculator
{
    public const string Version = "daily-utc-v1";
    public static List<DailyResult> Calculate(IReadOnlyList<Measurement> data, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var lookup = data.ToDictionary(x => (x.BuildingId, x.Timestamp));
        var result = new List<DailyResult>();
        foreach (var building in data.Select(x => x.BuildingId).Distinct().Order(StringComparer.Ordinal))
        for (var day = start; day < end; day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            decimal sum = 0;
            var measured = 0;
            var missing = new List<DateTime>();
            var midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            for (var hour = 1; hour <= 24; hour++)
            {
                var endTime = midnight.AddHours(hour);
                if (lookup.TryGetValue((building, endTime), out var value)) { sum += value.ConsumptionKwh; measured++; }
                else missing.Add(endTime);
            }
            // A partial total is explicitly accompanied by completeness and missing hour ends.
            result.Add(new(building, day, measured == 0 ? null : sum, measured, 24, missing.ToArray()));
        }
        return result;
    }
}
