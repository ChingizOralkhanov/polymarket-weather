using System.Globalization;
using PolymarketWeather.Core.Polymarket.Clob;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Backfill;

/// <summary>
/// Downloads Yes-token price history for every resolved bucket that has not been fetched yet.
/// Safe to re-run: finished buckets are recorded in price_fetches and skipped.
/// </summary>
public sealed class PricesBackfill(ClobClient clob, WeatherDb db, ILogger<PricesBackfill> logger)
{
    private const int Parallelism = 4;

    public async Task RunAsync(int fidelityMinutes, string? kind, IReadOnlyCollection<string> cities, CancellationToken ct)
    {
        await db.EnsureSchemaAsync(ct);
        var pending = await db.GetPendingPriceFetchesAsync(kind, cities, ct);
        logger.LogInformation("{Count} buckets pending, fidelity {Fidelity} min", pending.Count, fidelityMinutes);

        var done = 0;
        var points = 0L;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (p, token) =>
        {
            var (from, to) = GetRange(p);
            try
            {
                var history = await clob.GetPriceHistoryAsync(p.YesTokenId, from, to, fidelityMinutes, token);
                await db.SavePricesAsync(p.MarketId, fidelityMinutes, history.Select(h => (h.Time.ToUnixTimeSeconds(), h.Price)).ToList(), token);
                Interlocked.Add(ref points, history.Count);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Market {MarketId}: {Message}", p.MarketId, ex.Message);
            }

            var n = Interlocked.Increment(ref done);
            if (n % 500 == 0)
                logger.LogInformation("{Done}/{Total} buckets, {Points} points", n, pending.Count, Interlocked.Read(ref points));
        });

        logger.LogInformation("Done: {Done} buckets, {Points} points", done, points);
    }

    private static (DateTimeOffset From, DateTimeOffset To) GetRange(PendingPriceFetch p)
    {
        var date = DateOnly.ParseExact(p.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var from = TryParse(p.CreatedAt) ?? dayStart.AddDays(-3);
        var to = TryParse(p.ClosedTime) ?? dayStart.AddDays(2);
        return (from.AddHours(-1), to.AddHours(1));
    }

    private static DateTimeOffset? TryParse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
