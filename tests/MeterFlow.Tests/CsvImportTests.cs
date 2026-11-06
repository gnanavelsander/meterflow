using System.Text;
using MeterFlow.Api.Datasets;
using MeterFlow.Api.Infrastructure;
namespace MeterFlow.Tests;
public class CsvImportTests
{
    internal const string Header = "building_id,timestamp,consumption_kwh\n";
    internal const string Good = Header + "A,2026-01-01T01:00:00Z,0\nA,2026-01-01T02:00:00Z,2.5\n";
    [Fact] public void Valid_file_preserves_zero_precision_and_hash()
    {
        var parsed = CsvImport.Parse(Encoding.UTF8.GetBytes(Good));
        Assert.Equal(0m, parsed.Measurements[0].ConsumptionKwh);
        Assert.Equal(2.5m, parsed.Measurements[1].ConsumptionKwh);
        Assert.Equal(DateTimeKind.Utc, parsed.Measurements[0].Timestamp.Kind);
        Assert.Equal(64, parsed.Sha256.Length);
    }
    [Theory]
    [InlineData("A,2026-01-01T01:00:00Z,-1")]
    [InlineData("A,2026-01-01T01:00:00Z,NaN")]
    [InlineData("A,2026-01-01T01:30:00Z,1")]
    [InlineData("A,2026-01-01T01:00:00+01:00,1")]
    [InlineData("=formula,2026-01-01T01:00:00Z,1")]
    [InlineData("A,2026-01-01T01:00:00Z,0.0000001")]
    [InlineData("A,2026-01-01T01:00:00Z,1,extra")]
    [InlineData("\"A,2026-01-01T01:00:00Z,1")]
    public void Invalid_rows_report_row_number(string row)
    {
        var error = Assert.Throws<ApiFault>(() => CsvImport.Parse(Encoding.UTF8.GetBytes(Header + row + "\n")));
        Assert.Equal(422, error.Status);
        Assert.Contains((List<RowError>)error.Errors!, e => e.Row >= 2);
    }
    [Theory]
    [InlineData("")]
    [InlineData("building_id,timestamp\n")]
    [InlineData("building_id,timestamp,timestamp\n")]
    [InlineData(Header)]
    [InlineData(Good + "A,2026-01-01T01:00:00Z,9\n")]
    public void Invalid_files_are_rejected(string text) => Assert.Throws<ApiFault>(() => CsvImport.Parse(Encoding.UTF8.GetBytes(text)));
    [Fact] public async Task Limits_and_encoding_are_enforced()
    {
        Assert.Equal(413, (await Assert.ThrowsAsync<ApiFault>(() => CsvImport.ReadAsync(new MemoryStream(new byte[CsvImport.MaxBytes + 1])))).Status);
        Assert.Equal(422, Assert.Throws<ApiFault>(() => CsvImport.Parse([0xff])).Status);
        var many = Header + string.Concat(Enumerable.Range(1, CsvImport.MaxRows + 1).Select(i => $"A,{DateTime.UnixEpoch.AddHours(i):yyyy-MM-ddTHH:mm:ssZ},1\n"));
        Assert.Contains((List<RowError>)Assert.Throws<ApiFault>(() => CsvImport.Parse(Encoding.UTF8.GetBytes(many))).Errors!, e => e.Reason.Contains("50000"));
    }
}
