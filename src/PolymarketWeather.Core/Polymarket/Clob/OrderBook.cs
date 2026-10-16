using System.Text.Json.Serialization;

namespace PolymarketWeather.Core.Polymarket.Clob;

public sealed class OrderBook
{
    [JsonPropertyName("asset_id")] public string AssetId { get; init; } = "";
    [JsonPropertyName("market")] public string Market { get; init; } = "";
    [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
    [JsonPropertyName("bids")] public List<BookLevel> Bids { get; init; } = [];
    [JsonPropertyName("asks")] public List<BookLevel> Asks { get; init; } = [];

    // The API sorts bids ascending and asks descending, so the best levels are at the end.
    public IEnumerable<BookLevel> BestBids => Bids.OrderByDescending(l => l.Price);
    public IEnumerable<BookLevel> BestAsks => Asks.OrderBy(l => l.Price);
}

public sealed class BookLevel
{
    [JsonPropertyName("price")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Price { get; init; }

    [JsonPropertyName("size")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Size { get; init; }
}
