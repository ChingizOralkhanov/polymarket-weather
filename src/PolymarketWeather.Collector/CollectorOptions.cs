namespace PolymarketWeather.Collector;

public sealed class CollectorOptions
{
    public const string Section = "Collector";

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}
