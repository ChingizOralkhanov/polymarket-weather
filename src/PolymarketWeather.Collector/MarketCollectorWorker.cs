using Microsoft.Extensions.Options;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Polymarket.Gamma;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Collector;

/// <summary>Keeps open weather and post-count events in the database, and closes the ones that have resolved.</summary>
public sealed class MarketCollectorWorker(
    GammaClient gamma,
    WeatherDb db,
    IOptions<CollectorOptions> options,
    ILogger<MarketCollectorWorker> logger) : PeriodicWorker(logger)
{
    private const int MaxStaleRefreshPerCycle = 100;

    protected override TimeSpan Interval => options.Value.MarketsInterval;

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var weather = await gamma.GetOpenEventsByTagAsync(GammaClient.DailyTemperatureTag, ct);
        var count = new List<GammaEvent>();
        foreach (var seriesId in options.Value.CountSeriesIds)
            count.AddRange(await gamma.GetEventsBySeriesAsync(seriesId, closed: false, ct));

        // Events that left the open list keep closed = 0 until refreshed one by one.
        var openSlugs = weather.Concat(count).Select(e => e.Slug).ToHashSet();
        var stale = (await db.GetOpenEventSlugsAsync(ct)).Where(s => !openSlugs.Contains(s)).Take(MaxStaleRefreshPerCycle).ToList();
        foreach (var slug in stale)
        {
            var ev = await gamma.GetEventBySlugAsync(slug, ct);
            if (ev is null)
                continue;
            if (ev.Slug.Contains("temperature", StringComparison.Ordinal))
                weather = [.. weather, ev];
            else
                count.Add(ev);
        }

        var weatherParsed = Parse(weather, (GammaEvent e, out TemperatureEvent? r, out string? err) => TemperatureMarketParser.TryParse(e, out r, out err));
        var countParsed = Parse(count, (GammaEvent e, out CountEvent? r, out string? err) => CountMarketParser.TryParse(e, out r, out err));
        await db.UpsertEventsAsync(weatherParsed, ct);
        await db.UpsertCountEventsAsync(countParsed, ct);

        logger.LogInformation("Markets: {Weather} weather, {Count} post-count events stored, {Stale} refreshed after leaving the open list",
            weatherParsed.Count, countParsed.Count, stale.Count);
    }

    private delegate bool TryParser<T>(GammaEvent ev, out T? result, out string? error);

    private List<T> Parse<T>(IEnumerable<GammaEvent> events, TryParser<T> tryParse) where T : class
    {
        var result = new List<T>();
        foreach (var ev in events.DistinctBy(e => e.Id))
        {
            if (tryParse(ev, out var parsed, out var error))
                result.Add(parsed!);
            else
                logger.LogDebug("Skipped {Slug}: {Error}", ev.Slug, error);
        }
        return result;
    }
}
