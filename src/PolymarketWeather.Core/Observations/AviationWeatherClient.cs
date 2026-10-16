using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PolymarketWeather.Core.Observations;

public sealed record LiveMetar(string StationId, DateTimeOffset ObservedAt, DateTimeOffset? ReceivedAt, double? TempC, string? Raw);

/// <summary>Latest METARs from aviationweather.gov (NOAA). Temperatures carry tenths where the station reports a T-group.</summary>
public sealed class AviationWeatherClient(HttpClient http)
{
    public const string DefaultBaseAddress = "https://aviationweather.gov/";

    public async Task<IReadOnlyList<LiveMetar>> GetMetarsAsync(IReadOnlyCollection<string> stationIds, int hours, CancellationToken ct = default)
    {
        if (stationIds.Count == 0)
            return [];

        var url = $"api/data/metar?ids={Uri.EscapeDataString(string.Join(',', stationIds))}&format=json&hours={hours}";
        using var response = await http.GetAsync(url, ct);
        // 204 means no reports in the window.
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return [];
        response.EnsureSuccessStatusCode();

        var rows = await response.Content.ReadFromJsonAsync<List<Row>>(ct) ?? [];
        return rows
            .Where(r => r.IcaoId is not null && r.ObsTime > 0)
            .Select(r => new LiveMetar(r.IcaoId!, DateTimeOffset.FromUnixTimeSeconds(r.ObsTime), r.ReceiptTime, r.Temp, r.RawOb))
            .ToList();
    }

    private sealed class Row
    {
        [JsonPropertyName("icaoId")] public string? IcaoId { get; init; }
        [JsonPropertyName("obsTime")] public long ObsTime { get; init; }
        [JsonPropertyName("receiptTime")] public DateTimeOffset? ReceiptTime { get; init; }
        [JsonPropertyName("temp")] public double? Temp { get; init; }
        [JsonPropertyName("rawOb")] public string? RawOb { get; init; }
    }
}
