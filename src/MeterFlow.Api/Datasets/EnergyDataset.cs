using MeterFlow.Api.Projects;
namespace MeterFlow.Api.Datasets;
public sealed class EnergyDataset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public EnergyProject Project { get; set; } = null!;
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public int RowCount { get; set; }
}
public sealed class Measurement
{
    public Guid DatasetId { get; set; }
    public EnergyDataset Dataset { get; set; } = null!;
    public string BuildingId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public decimal ConsumptionKwh { get; set; }
}
