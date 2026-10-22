using Microsoft.Extensions.Options;
using PolymarketWeather.Core.Observations;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Collector;

/// <summary>Polls the latest METARs of stations that resolve currently open weather markets.</summary>
public sealed class MetarCollectorWorker(
    AviationWeatherClient aviationWeather,
    WeatherDb db,
    IOptions<CollectorOptions> options,
    ILogger<MetarCollectorWorker> logger) : PeriodicWorker(logger)
{
    private const int BatchSize = 40;
    private const int LookbackHours = 3;

    // The first cycle after a start covers a whole local day, so the daily max (strategy W-B) starts complete.
    // Smaller batches keep that larger request under the API gateway timeout.
    private const int StartupLookbackHours = 30;
    private const int StartupBatchSize = 10;

    private bool _started;

    protected override TimeSpan Interval => options.Value.MetarInterval;

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var stations = await db.GetLiveStationsAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var now = DateTimeOffset.UtcNow;
        var hours = _started ? LookbackHours : StartupLookbackHours;

        var added = 0;
        foreach (var batch in stations.Chunk(_started ? BatchSize : StartupBatchSize))
            added += await db.InsertLiveMetarsAsync(await aviationWeather.GetMetarsAsync(batch, hours, ct), now, ct);
        _started = true;

        if (added > 0)
            logger.LogInformation("METAR: {Added} new reports from {Stations} stations", added, stations.Count);
    }
}
