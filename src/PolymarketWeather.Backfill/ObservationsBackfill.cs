using PolymarketWeather.Core.Observations;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Backfill;

/// <summary>
/// Loads station metadata and METAR history from IEM for every station that resolves a market.
/// Re-runs continue from the last stored observation.
/// </summary>
public sealed class ObservationsBackfill(IemClient iem, WeatherDb db, ILogger<ObservationsBackfill> logger)
{
    // IEM asks for at most ~1 request per second per IP.
    private static readonly TimeSpan RequestDelay = TimeSpan.FromSeconds(1.5);

    public async Task RunAsync(CancellationToken ct)
    {
        await db.EnsureSchemaAsync(ct);
        var stations = await db.GetResolutionStationsAsync(ct);
        logger.LogInformation("{Count} stations to load", stations.Count);

        foreach (var (stationId, fromDate, toDate) in stations)
        {
            if (!await db.HasStationAsync(stationId, ct))
            {
                var info = await iem.GetStationAsync(stationId, ct);
                await Task.Delay(RequestDelay, ct);
                if (info is null)
                {
                    logger.LogWarning("{Station}: not found in IEM", stationId);
                    continue;
                }
                await db.UpsertStationAsync(info, ct);
            }

            // One extra day on both sides covers stations whose local day spans two UTC days.
            var to = Min(toDate.AddDays(1), DateOnly.FromDateTime(DateTime.UtcNow));
            var last = await db.GetLastObservationAsync(stationId, ct);
            var lastDay = last is null ? (DateOnly?)null : DateOnly.FromDateTime(last.Value.UtcDateTime);
            if (lastDay >= to)
                continue;
            var from = lastDay ?? fromDate.AddDays(-1);

            var observations = await iem.GetMetarsAsync(stationId, from, to, ct);
            await Task.Delay(RequestDelay, ct);
            await db.SaveObservationsAsync(stationId, observations, ct);
            logger.LogInformation("{Station}: {Count} METARs {From}..{To}", stationId, observations.Count, from, to);
        }

        logger.LogInformation("Done");
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
