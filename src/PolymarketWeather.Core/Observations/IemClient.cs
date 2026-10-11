using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PolymarketWeather.Core.Observations;

public sealed record StationInfo(string StationId, string Name, double Latitude, double Longitude, double? Elevation, string TimeZone);

public sealed record MetarObservation(DateTimeOffset Time, double? TempC, double? TempF, string? Metar);

/// <summary>
/// Iowa Environmental Mesonet: worldwide METAR archive. Limit is about one request per second per IP.
/// </summary>
public sealed class IemClient(HttpClient http)
{
    public const string DefaultBaseAddress = "https://mesonet.agron.iastate.edu/";

    public async Task<StationInfo?> GetStationAsync(string icao, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/1/station/{Uri.EscapeDataString(icao)}.json", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadFromJsonAsync<StationResponse>(ct);
        var s = body?.Data.FirstOrDefault();
        return s is null ? null : new StationInfo(icao, s.Name ?? icao, s.Latitude, s.Longitude, s.Elevation, s.TzName ?? "UTC");
    }

    /// <summary>Routine and special METARs (report types 3 and 4) between two UTC days, inclusive.</summary>
    public async Task<IReadOnlyList<MetarObservation>> GetMetarsAsync(string icao, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default)
    {
        var url = "cgi-bin/request/asos.py"
                  + $"?station={Uri.EscapeDataString(icao)}&data=tmpc&data=tmpf&data=metar&tz=UTC&format=onlycomma&missing=empty"
                  + $"&year1={fromUtc.Year}&month1={fromUtc.Month}&day1={fromUtc.Day}"
                  + $"&year2={toUtc.Year}&month2={toUtc.Month}&day2={toUtc.Day}"
                  + "&report_type=3&report_type=4";

        var csv = await http.GetStringAsync(url, ct);
        return ParseCsv(csv);
    }

    // station,valid,tmpc,tmpf,metar — the METAR is the last column and contains no commas.
    internal static IReadOnlyList<MetarObservation> ParseCsv(string csv)
    {
        var result = new List<MetarObservation>();
        using var reader = new StringReader(csv);
        reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',', 5);
            if (parts.Length < 5)
                continue;
            if (!DateTime.TryParseExact(parts[1], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                continue;

            result.Add(new MetarObservation(
                new DateTimeOffset(time, TimeSpan.Zero),
                ParseDouble(parts[2]),
                ParseDouble(parts[3]),
                string.IsNullOrWhiteSpace(parts[4]) ? null : parts[4]));
        }
        return result;
    }

    private static double? ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private sealed class StationResponse
    {
        [JsonPropertyName("data")] public List<StationRow> Data { get; init; } = [];
    }

    private sealed class StationRow
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("latitude")] public double Latitude { get; init; }
        [JsonPropertyName("longitude")] public double Longitude { get; init; }
        [JsonPropertyName("elevation")] public double? Elevation { get; init; }
        [JsonPropertyName("tzname")] public string? TzName { get; init; }
    }
}
