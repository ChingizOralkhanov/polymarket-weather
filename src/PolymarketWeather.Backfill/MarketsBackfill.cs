using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Polymarket.Gamma;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Backfill;

/// <summary>
/// Loads every daily temperature event (closed and open) of every city series into the database.
/// Older events are not tagged "daily-temperature", so the series are the only complete index.
/// </summary>
public sealed class MarketsBackfill(GammaClient gamma, WeatherDb db, ILogger<MarketsBackfill> logger)
{
    public async Task RunAsync(string? seriesFilter, CancellationToken ct)
    {
        await db.EnsureSchemaAsync(ct);

        var series = (await gamma.GetAllSeriesAsync(ct))
            .Where(IsDailyTemperatureSeries)
            .Where(s => seriesFilter is null || s.Slug.Contains(seriesFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Slug)
            .ToList();
        logger.LogInformation("Found {Count} daily temperature series", series.Count);

        int totalParsed = 0, totalSkipped = 0;
        foreach (var s in series)
        {
            var events = new List<GammaEvent>();
            events.AddRange(await gamma.GetEventsBySeriesAsync(s.Id, closed: true, ct));
            events.AddRange(await gamma.GetEventsBySeriesAsync(s.Id, closed: false, ct));

            var parsed = new List<TemperatureEvent>(events.Count);
            var skipped = new List<(string, string?, string)>();
            foreach (var ev in events)
            {
                if (TemperatureMarketParser.TryParse(ev, out var te, out var error))
                    parsed.Add(te! with { SeriesId = te.SeriesId ?? s.Id });
                else
                    skipped.Add((ev.Slug, ev.Id, error!));
            }

            await db.UpsertEventsAsync(parsed, ct);
            await db.UpsertSkippedAsync(skipped, ct);
            totalParsed += parsed.Count;
            totalSkipped += skipped.Count;

            logger.LogInformation(
                "{Series}: {Parsed} events stored, {Skipped} skipped, range {From}..{To}",
                s.Slug,
                parsed.Count,
                skipped.Count,
                parsed.Count > 0 ? parsed.Min(e => e.Date) : null,
                parsed.Count > 0 ? parsed.Max(e => e.Date) : null);
        }

        logger.LogInformation("Done: {Parsed} events stored, {Skipped} skipped", totalParsed, totalSkipped);
    }

    private static bool IsDailyTemperatureSeries(GammaSeries s) =>
        s.Slug.EndsWith("-daily-weather", StringComparison.OrdinalIgnoreCase)
        || s.Slug.EndsWith("-daily-lowest-temperature", StringComparison.OrdinalIgnoreCase);
}
