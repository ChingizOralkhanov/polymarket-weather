using System.Globalization;
using Microsoft.Extensions.Options;
using PolymarketWeather.Core.Polymarket.Clob;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Collector;

/// <summary>
/// Snapshots the top of the order book of active markets. A snapshot is stored when the best level changes,
/// and at least every <see cref="FullSnapshotEvery"/> so that deeper levels are sampled too.
/// </summary>
public sealed class BookSnapshotWorker(
    ClobClient clob,
    WeatherDb db,
    IOptions<CollectorOptions> options,
    ILogger<BookSnapshotWorker> logger) : PeriodicWorker(logger)
{
    private const int BatchSize = 100;
    private const int Depth = 5;

    private static readonly TimeSpan FullSnapshotEvery = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (string Top, long Ts)> _lastBooks = new();

    protected override TimeSpan Interval => options.Value.BooksInterval;

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var tokens = await db.GetActiveBookTokensAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var marketByToken = tokens.ToDictionary(t => t.TokenId, t => t.MarketId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var changed = new List<BookSnapshot>();
        foreach (var batch in tokens.Select(t => t.TokenId).Chunk(BatchSize))
        {
            foreach (var book in await clob.GetBooksAsync(batch, ct))
            {
                var bids = book.BestBids.Take(Depth).ToList();
                var asks = book.BestAsks.Take(Depth).ToList();
                var top = Format(bids.Take(1)) + "|" + Format(asks.Take(1));
                if (_lastBooks.TryGetValue(book.AssetId, out var last)
                    && last.Top == top
                    && now - last.Ts < (long)FullSnapshotEvery.TotalSeconds)
                    continue;

                _lastBooks[book.AssetId] = (top, now);
                var bidsText = Format(bids);
                var asksText = Format(asks);
                changed.Add(new BookSnapshot(
                    book.AssetId,
                    marketByToken.GetValueOrDefault(book.AssetId, book.Market),
                    now,
                    bids.FirstOrDefault()?.Price,
                    asks.FirstOrDefault()?.Price,
                    bids.FirstOrDefault()?.Size,
                    asks.FirstOrDefault()?.Size,
                    bidsText,
                    asksText));
            }
        }

        await db.SaveBookSnapshotsAsync(changed, ct);
        logger.LogInformation("Books: {Tokens} tokens polled, {Changed} changed", tokens.Count, changed.Count);
    }

    private static string Format(IEnumerable<BookLevel> levels) =>
        string.Join(';', levels.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Price}:{l.Size}")));
}
