using Microsoft.Extensions.DependencyInjection;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Musk;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Core.Tests;

public sealed class WeatherDbTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"weather-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _services = null!;
    private WeatherDb _db = null!;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection().AddWeatherDb($"Data Source={_path};Pooling=False").BuildServiceProvider();
        _db = _services.GetRequiredService<WeatherDb>();
        await _db.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
    }

    [Fact]
    public async Task UpsertEventsOverwritesStoredEventAndBuckets()
    {
        await _db.UpsertEventsAsync([Event(closed: false, BucketOutcome.Unresolved)]);
        await _db.UpsertEventsAsync([Event(closed: true, BucketOutcome.Yes)]);

        var pending = await _db.GetPendingPriceFetchesAsync(TemperatureKind.Max, ["nyc"]);

        var p = Assert.Single(pending);
        Assert.Equal("m1", p.MarketId);
        Assert.Equal(new DateOnly(2026, 10, 12), p.Date);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero), p.ClosedTime);
    }

    [Fact]
    public async Task SavedPricesTakeTheBucketOffThePendingList()
    {
        await _db.UpsertEventsAsync([Event(closed: true, BucketOutcome.Yes)]);

        await _db.SavePricesAsync("m1", 10, [(100, 0.4m), (200, 0.5m)]);
        await _db.SavePricesAsync("m1", 10, [(200, 0.6m), (300, 0.7m)]);

        Assert.Empty(await _db.GetPendingPriceFetchesAsync(null, null));
    }

    [Fact]
    public async Task InsertPostsCountsOnlyNewPosts()
    {
        var collected = DateTimeOffset.UtcNow;
        var first = new TrackedPost("1", "elonmusk", "a", new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), null);
        var second = new TrackedPost("2", "elonmusk", "b", new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero), null);

        Assert.Equal(1, await _db.InsertPostsAsync([first], collected));
        Assert.Equal(1, await _db.InsertPostsAsync([first, second, second], collected));
        Assert.Equal(second.CreatedAt, await _db.GetLastPostTimeAsync("elonmusk"));
    }

    private static TemperatureEvent Event(bool closed, BucketOutcome outcome) => new(
        "e1", "highest-temperature-in-nyc-on-october-12-2026", null, "nyc", TemperatureKind.Max,
        new DateOnly(2026, 10, 12), TemperatureUnit.Fahrenheit, ResolutionSourceKind.NoaaTimeseries, "KLGA",
        new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero),
        closed, 1000m, null,
        [new RangeBucket("m1", "c1", "y1", "n1", "60-61°F", 60, 61, outcome,
            closed ? new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero) : null)]);
}
