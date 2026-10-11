namespace PolymarketWeather.Core.Storage;

public sealed record ForecastDaily(
    string StationId,
    string Model,
    int LeadDays,
    DateOnly Date,
    double? TmaxC,
    double? TminC,
    int Hours);
