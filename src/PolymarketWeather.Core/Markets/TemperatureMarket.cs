namespace PolymarketWeather.Core.Markets;

public enum TemperatureKind { Max, Min }

public enum TemperatureUnit { Celsius, Fahrenheit }

public enum ResolutionSourceKind { NoaaTimeseries, HongKongObservatory, WeatherUnderground, Unknown }

public enum BucketOutcome { Unresolved, Yes, No }

public sealed record TemperatureEvent(
    string EventId,
    string Slug,
    string? SeriesId,
    string City,
    TemperatureKind Kind,
    DateOnly Date,
    TemperatureUnit Unit,
    ResolutionSourceKind SourceKind,
    string? StationId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? EndDate,
    bool Closed,
    decimal? Volume,
    string? Description,
    IReadOnlyList<TemperatureBucket> Buckets)
{
    public TemperatureBucket? WinningBucket => Buckets.SingleOrDefault(b => b.Outcome == BucketOutcome.Yes);
}

/// <summary>
/// Inclusive range in whole degrees of the event unit; a null bound means open-ended ("or below" / "or higher").
/// </summary>
public sealed record TemperatureBucket(
    string MarketId,
    string ConditionId,
    string YesTokenId,
    string NoTokenId,
    string Label,
    int? Lower,
    int? Upper,
    BucketOutcome Outcome,
    DateTimeOffset? ClosedTime)
{
    public bool Contains(int value) => (Lower is null || value >= Lower) && (Upper is null || value <= Upper);
}
