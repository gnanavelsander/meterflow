using MeterFlow.Api.Datasets;
namespace MeterFlow.Api.Jobs;
public sealed class AnalysisJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DatasetId { get; set; }
    public EnergyDataset Dataset { get; set; } = null!;
    public string OwnerId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDateExclusive { get; set; }
    public string AnalysisVersion { get; set; } = Analysis.AnalysisCalculator.Version;
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? ErrorCode { get; set; }
    public string? ResultJson { get; set; }
}
public sealed record CreateAnalysis(Guid DatasetId, DateOnly StartDate, DateOnly EndDateExclusive);
