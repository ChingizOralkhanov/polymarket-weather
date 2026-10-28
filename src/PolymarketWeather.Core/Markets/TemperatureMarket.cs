namespace PolymarketWeather.Core.Markets;

public enum TemperatureKind { Max, Min }

public enum TemperatureUnit { Celsius, Fahrenheit }

public enum ResolutionSourceKind { NoaaTimeseries, HongKongObservatory, WeatherUnderground, AviationWeatherMetar, Unknown }

public static class ResolutionSources
{
    /// <summary>Sources whose value is derived from the station's METAR reports, so live METARs can track them.</summary>
    public static readonly ResolutionSourceKind[] MetarBased =
    [
        ResolutionSourceKind.AviationWeatherMetar, ResolutionSourceKind.NoaaTimeseries, ResolutionSourceKind.WeatherUnderground,
    ];
}

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
    IReadOnlyList<RangeBucket> Buckets)
{
    public RangeBucket? WinningBucket => Buckets.SingleOrDefault(b => b.Outcome == BucketOutcome.Yes);
}

/// <summary>
/// One outcome market of a range event: inclusive bounds in whole units (degrees, posts);
/// a null bound means open-ended ("or below", "500+").
/// </summary>
public sealed record RangeBucket(
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
