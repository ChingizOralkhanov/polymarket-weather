using System.Globalization;
using Microsoft.Extensions.Options;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Polymarket.Clob;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Collector;

/// <summary>
/// Paper version of strategy W-B. Once the METAR max of the local day passes a bucket's upper bound, that bucket
/// cannot win; we record the order book right then to measure how much "No" could still be bought below $1.
/// </summary>
public sealed class WbPaperWorker(
    WeatherDb db,
    ClobClient clob,
    IOptions<CollectorOptions> options,
    ILogger<WbPaperWorker> logger) : PeriodicWorker(logger)
{
    private const int Depth = 5;
    private const decimal LowestBracketSuspectPrice = 0.5m;

    protected override TimeSpan Interval => options.Value.WbInterval;

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.GetWbCandidatesAsync(DateOnly.FromDateTime(now.UtcDateTime), ct);

        var dead = new List<(WbCandidate Event, WbBucket Bucket, int Max, LiveTemperature Metar)>();
        foreach (var e in candidates)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(e.TimeZone);
            var dayStart = new DateTimeOffset(e.Date.ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(e.Date.ToDateTime(TimeOnly.MinValue)));
            var dayEnd = dayStart.AddDays(1);
            if (now < dayStart || now > dayEnd.AddHours(6))
                continue;

            var metars = await db.GetLiveTemperaturesAsync(e.StationId, dayStart.ToUnixTimeSeconds(), dayEnd.ToUnixTimeSeconds(), ct);
            var runningMax = int.MinValue;
            LiveTemperature? maxMetar = null;
            foreach (var m in metars)
            {
                var value = ResolutionRounding.ToBucketValue(m.TempC, e.Unit);
                if (value > runningMax)
                    (runningMax, maxMetar) = (value, m);
            }
            if (maxMetar is null)
                continue;

            dead.AddRange(e.Buckets.Where(b => b.Upper < runningMax).Select(b => (e, b, runningMax, maxMetar)));
        }

        if (dead.Count == 0)
            return;

        var tokens = dead.SelectMany(d => new[] { d.Bucket.YesTokenId, d.Bucket.NoTokenId })
            .Concat(dead.Select(d => d.Event.Lowest?.YesTokenId).OfType<string>())
            .Distinct()
            .ToList();
        var books = new Dictionary<string, OrderBook>();
        foreach (var batch in tokens.Chunk(100))
            foreach (var book in await clob.GetBooksAsync(batch, ct))
                books[book.AssetId] = book;

        var detectedAt = DateTimeOffset.UtcNow;
        var signals = dead.Select(d =>
        {
            var yesBid = books.GetValueOrDefault(d.Bucket.YesTokenId)?.BestBids.FirstOrDefault();
            var noAsks = books.GetValueOrDefault(d.Bucket.NoTokenId)?.BestAsks.Take(Depth).ToList() ?? [];
            var warning = SuspectNoDataResolution(d.Event, d.Max, books);
            return new WbSignalRow
            {
                MarketId = d.Bucket.MarketId,
                EventId = d.Event.EventId,
                City = d.Event.City,
                StationId = d.Event.StationId,
                Date = d.Event.Date,
                Label = d.Bucket.Label,
                Upper = d.Bucket.Upper,
                ObservedMax = d.Max,
                MetarTs = d.Metar.Ts,
                MetarReceivedAt = d.Metar.ReceivedAt,
                DetectedAt = detectedAt,
                YesBestBid = (double?)yesBid?.Price,
                YesBidSize = (double?)yesBid?.Size,
                NoBestAsk = (double?)noAsks.FirstOrDefault()?.Price,
                NoAskSize = (double?)noAsks.FirstOrDefault()?.Size,
                NoAsks = string.Join(';', noAsks.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Price}:{l.Size}"))),
                Warning = warning,
            };
        }).ToList();

        await db.InsertWbSignalsAsync(signals, ct);

        // A dead bucket whose No still sells below 99c is a would-be trade.
        var cheap = signals.Where(s => s.NoBestAsk is < 0.99).ToList();
        logger.LogInformation("W-B: {Dead} dead buckets recorded, {Cheap} with No below 99c ({Warned} with a warning)",
            signals.Count, cheap.Count, cheap.Count(s => s.Warning is not null));
        foreach (var s in cheap)
            logger.LogInformation("W-B would buy No: {City} {Date} [{Label}] METAR max {Max}, No ask {Ask} x {Size}{Warning}",
                s.City, s.Date, s.Label, s.ObservedMax, s.NoBestAsk, s.NoAskSize, s.Warning is null ? "" : $"; warning: {s.Warning}");
    }

    /// <summary>
    /// When neither NOAA nor Weather Underground has data, the market resolves to its lowest bracket. If METAR has
    /// already passed that bracket but the crowd still prices it high, the crowd may expect exactly that. Only a
    /// warning: on Istanbul 21-22.10 the crowd was wrong and the real temperature resolved the market.
    /// </summary>
    private static string? SuspectNoDataResolution(WbCandidate e, int observedMax, IReadOnlyDictionary<string, OrderBook> books)
    {
        if (e.Lowest is null || e.Lowest.Upper >= observedMax)
            return null;

        var lowestBid = books.GetValueOrDefault(e.Lowest.YesTokenId)?.BestBids.FirstOrDefault()?.Price;
        return lowestBid >= LowestBracketSuspectPrice
            ? string.Create(CultureInfo.InvariantCulture, $"lowest bracket [{e.Lowest.Label}] bid {lowestBid} although METAR max is {observedMax}: market expects a no-data resolution")
            : null;
    }

}
