using PolymarketWeather.Core.Markets;

namespace PolymarketWeather.Core.Storage;

public sealed class EventRow
{
    public required string EventId { get; set; }
    public required string Slug { get; set; }
    public string? SeriesId { get; set; }
    public required string City { get; set; }
    public TemperatureKind Kind { get; set; }
    public DateOnly Date { get; set; }
    public TemperatureUnit Unit { get; set; }
    public ResolutionSourceKind SourceKind { get; set; }
    public string? StationId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public bool Closed { get; set; }
    public double? Volume { get; set; }
    public string? WinningMarketId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BucketRow
{
    public required string MarketId { get; set; }
    public required string EventId { get; set; }
    public int Position { get; set; }
    public required string ConditionId { get; set; }
    public required string YesTokenId { get; set; }
    public required string NoTokenId { get; set; }
    public required string Label { get; set; }
    public int? Lower { get; set; }
    public int? Upper { get; set; }
    public BucketOutcome Outcome { get; set; }
    public DateTimeOffset? ClosedTime { get; set; }
}

/// <summary>Yes-token price; the No token is its complement.</summary>
public sealed class PriceRow
{
    public required string MarketId { get; set; }
    public long Ts { get; set; }
    public double Price { get; set; }
}

public sealed class PriceFetchRow
{
    public required string MarketId { get; set; }
    public int Fidelity { get; set; }
    public int Points { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

public sealed class StationRow
{
    public required string StationId { get; set; }
    public required string Name { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Elevation { get; set; }
    public required string TimeZone { get; set; }
}

public sealed class ObservationRow
{
    public required string StationId { get; set; }
    public long Ts { get; set; }
    public double? TempC { get; set; }
    public double? TempF { get; set; }
    public string? Metar { get; set; }
}

/// <summary>Daily extremes over the station's local day, from hourly values of a model run made LeadDays earlier.</summary>
public sealed class ForecastDailyRow
{
    public required string StationId { get; set; }
    public required string Model { get; set; }
    public int LeadDays { get; set; }
    public DateOnly Date { get; set; }
    public double? TmaxC { get; set; }
    public double? TminC { get; set; }
    public int Hours { get; set; }
}

public sealed class SkippedEventRow
{
    public required string Slug { get; set; }
    public string? EventId { get; set; }
    public required string Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CountEventRow
{
    public required string EventId { get; set; }
    public required string Slug { get; set; }
    public string? SeriesId { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public bool Closed { get; set; }
    public double? Volume { get; set; }
    public string? WinningMarketId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CountBucketRow
{
    public required string MarketId { get; set; }
    public required string EventId { get; set; }
    public int Position { get; set; }
    public required string ConditionId { get; set; }
    public required string YesTokenId { get; set; }
    public required string NoTokenId { get; set; }
    public required string Label { get; set; }
    public int? Lower { get; set; }
    public int? Upper { get; set; }
    public BucketOutcome Outcome { get; set; }
    public DateTimeOffset? ClosedTime { get; set; }
}

/// <summary>Counting window of xtracker; MarketSlug links it to its count event.</summary>
public sealed class TrackingRow
{
    public required string TrackingId { get; set; }
    public required string Handle { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public string? MarketSlug { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>CollectedAt is when we saw the post, to compare with ImportedAt (when xtracker counted it).</summary>
public sealed class PostRow
{
    public required string PlatformId { get; set; }
    public required string Handle { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public string? Content { get; set; }
}

/// <summary>Top of book per outcome token, written only when it changes. Bids/Asks: "price:size" pairs, best first.</summary>
public sealed class BookSnapshotRow
{
    public required string TokenId { get; set; }
    public long Ts { get; set; }
    public required string MarketId { get; set; }
    public double? BestBid { get; set; }
    public double? BestAsk { get; set; }
    public double? BidSize { get; set; }
    public double? AskSize { get; set; }
    public required string Bids { get; set; }
    public required string Asks { get; set; }
}

/// <summary>
/// Paper signal of strategy W-B: the observed METAR max passed the bucket's upper bound, so its Yes cannot win.
/// One row per bucket, with the order book at the moment we noticed it. Asks: "price:size" pairs, best first.
/// </summary>
public sealed class WbSignalRow
{
    public required string MarketId { get; set; }
    public required string EventId { get; set; }
    public required string City { get; set; }
    public required string StationId { get; set; }
    public DateOnly Date { get; set; }
    public required string Label { get; set; }
    public int Upper { get; set; }
    public int ObservedMax { get; set; }
    public long MetarTs { get; set; }
    public DateTimeOffset? MetarReceivedAt { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public double? YesBestBid { get; set; }
    public double? YesBidSize { get; set; }
    public double? NoBestAsk { get; set; }
    public double? NoAskSize { get; set; }
    public required string NoAsks { get; set; }

    /// <summary>Why this signal would not be traded, e.g. the crowd expects a "no data" resolution; null if tradable.</summary>
    public string? SkipReason { get; set; }
}

public sealed class MetarLiveRow
{
    public required string StationId { get; set; }
    public long Ts { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public double? TempC { get; set; }
    public string? Raw { get; set; }
}
