using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using MeterFlow.Api.Infrastructure;
namespace MeterFlow.Api.Datasets;
public sealed record RowError(int Row, string Reason);
public sealed record ParsedCsv(string Sha256, List<Measurement> Measurements);
public static partial class CsvImport
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 50000;
    public const int MaxBuildings = 100;
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex BuildingPattern();
    public static async Task<ParsedCsv> ReadAsync(Stream input, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + count > MaxBytes) throw new ApiFault(413, "CSV too large", "Maximum upload size is 5 MiB.");
            bytes.Write(buffer, 0, count);
        }
        return Parse(bytes.ToArray());
    }
    public static ParsedCsv Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new ApiFault(413, "CSV too large", "Maximum upload size is 5 MiB.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw Invalid([new(1, "The file must be UTF-8.")]); }
        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            IgnoreBlankLines = false, ExceptionMessagesContainRawData = false,
            LineBreakInQuotedFieldIsBadData = true
        });
        var errors = new List<RowError>();
        var result = new List<Measurement>();
        var keys = new HashSet<(string, DateTime)>();
        var buildings = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!csv.Read()) throw Invalid([new(1, "The file is empty.")]);
            csv.ReadHeader();
            string[] required = ["building_id", "timestamp", "consumption_kwh"];
            if (csv.HeaderRecord is not { Length: 3 } headers || !headers.Order().SequenceEqual(required.Order()))
                throw Invalid([new(1, "Expected exactly building_id,timestamp,consumption_kwh (in any order).")]);
            var rows = 0;
            while (csv.Read())
            {
                var row = csv.Parser.RawRow;
                if (++rows > MaxRows) throw Invalid([new(row, "Maximum 50000 measurement rows exceeded.")]);
                if (csv.Parser.Count != 3) { errors.Add(new(row, "Expected exactly three fields.")); }
                else
                {
                    var building = csv.GetField("building_id") ?? "";
                    var stamp = csv.GetField("timestamp");
                    var value = csv.GetField("consumption_kwh") ?? "";
                    var valid = true;
                    if (!BuildingPattern().IsMatch(building))
                    { errors.Add(new(row, "building_id must be 1..64 ASCII letters/digits, dot, underscore or hyphen; start with a letter/digit.")); valid = false; }
                    if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
                        || timestamp.Minute != 0 || timestamp.Second != 0 || timestamp <= DateTime.UnixEpoch)
                    { errors.Add(new(row, "timestamp must be a UTC hour end after 1970-01-01T00:00:00Z, formatted yyyy-MM-ddTHH:00:00Z.")); valid = false; }
                    if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var kwh)
                        || kwh < 0 || kwh > 1000000000 || value.Count(c => c == '.') > 1
                        || (value.Contains('.') && value.Length - value.IndexOf('.') - 1 > 6))
                    { errors.Add(new(row, "consumption_kwh must be 0..1000000000, using a decimal point and at most six decimal places.")); valid = false; }
                    if (valid)
                    {
                        if (!keys.Add((building, timestamp))) errors.Add(new(row, "Duplicate building_id and timestamp."));
                        buildings.Add(building);
                        if (buildings.Count > MaxBuildings) throw Invalid([new(row, "Maximum 100 distinct buildings exceeded.")]);
                        result.Add(new() { BuildingId = building, Timestamp = timestamp, ConsumptionKwh = kwh });
                    }
                }
                if (errors.Count >= 100) break;
            }
        }
        catch (CsvHelperException) { errors.Add(new(csv.Parser.RawRow, "Malformed CSV quoting or field structure.")); }
        if (errors.Count > 0) throw Invalid(errors.Take(100).ToList());
        if (result.Count == 0) throw Invalid([new(2, "At least one measurement is required.")]);
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), result);
    }
    private static ApiFault Invalid(List<RowError> errors) => new(422, "Invalid CSV", "The whole import was rejected. Up to 100 row errors are reported; no measurements were stored.", errors);
}
