namespace PolymarketWeather.Core.Storage;

public sealed record PendingPriceFetch(
    string MarketId,
    string YesTokenId,
    DateTimeOffset? CreatedAt,
    DateOnly Date,
    DateTimeOffset? ClosedTime);
