namespace PolymarketWeather.Core.Storage;

public sealed class PendingPriceFetch
{
    public string MarketId { get; init; } = "";
    public string YesTokenId { get; init; } = "";
    public string? CreatedAt { get; init; }
    public string Date { get; init; } = "";
    public string? ClosedTime { get; init; }
}
