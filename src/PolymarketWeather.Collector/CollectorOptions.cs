namespace PolymarketWeather.Collector;

public sealed class CollectorOptions
{
    public const string Section = "Collector";

    public TimeSpan MarketsInterval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan BooksInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan PostsInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MetarInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan WbInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gamma series of the post-count markets: weekly, daily, 48 h.</summary>
    public string[] CountSeriesIds { get; set; } = ["10000", "10181", "10816"];

    public string PostsHandle { get; set; } = "elonmusk";
}
