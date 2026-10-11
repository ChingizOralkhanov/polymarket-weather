using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PolymarketWeather.Core.Polymarket.Clob;

public sealed record PricePoint(DateTimeOffset Time, decimal Price);

public sealed class ClobClient(HttpClient http)
{
    public const string DefaultBaseAddress = "https://clob.polymarket.com/";

    /// <summary>
    /// Price history of one outcome token. An explicit time range is required:
    /// for resolved markets "interval=max" returns an empty series.
    /// </summary>
    public async Task<IReadOnlyList<PricePoint>> GetPriceHistoryAsync(
        string tokenId, DateTimeOffset from, DateTimeOffset to, int fidelityMinutes, CancellationToken ct = default)
    {
        var url = $"prices-history?market={Uri.EscapeDataString(tokenId)}"
                  + $"&startTs={from.ToUnixTimeSeconds()}&endTs={to.ToUnixTimeSeconds()}&fidelity={fidelityMinutes}";
        var response = await http.GetFromJsonAsync<PriceHistoryResponse>(url, ct);
        return response?.History.Select(p => new PricePoint(DateTimeOffset.FromUnixTimeSeconds(p.T), p.P)).ToList() ?? [];
    }

    private sealed class PriceHistoryResponse
    {
        [JsonPropertyName("history")] public List<RawPoint> History { get; init; } = [];
    }

    private sealed class RawPoint
    {
        [JsonPropertyName("t")] public long T { get; init; }
        [JsonPropertyName("p")] public decimal P { get; init; }
    }
}
