namespace PolymarketWeather.Core.Markets;

public enum TemperatureKind { Max, Min }

public enum TemperatureUnit { Celsius, Fahrenheit }

public enum ResolutionSourceKind { NoaaTimeseries, HongKongObservatory, WeatherUnderground, Unknown }

public sealed record TemperatureEvent(
    string EventId,
    string Slug,
    string City,
    TemperatureKind Kind,
    DateOnly Date,
    TemperatureUnit Unit,
    ResolutionSourceKind SourceKind,
    string? StationId,
    DateTimeOffset? EndDate,
    IReadOnlyList<TemperatureBucket> Buckets);

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
    int? Upper)
{
    public bool Contains(int value) => (Lower is null || value >= Lower) && (Upper is null || value <= Upper);
}
