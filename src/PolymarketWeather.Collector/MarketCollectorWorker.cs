using Microsoft.Extensions.Options;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Collector;

public sealed class MarketCollectorWorker(
    GammaClient gamma,
    IOptions<CollectorOptions> options,
    ILogger<MarketCollectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await CollectOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Collection cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectOnceAsync(CancellationToken ct)
    {
        var events = await gamma.GetOpenEventsByTagAsync(GammaClient.DailyTemperatureTag, ct);

        var parsed = new List<TemperatureEvent>(events.Count);
        foreach (var ev in events)
        {
            if (TemperatureMarketParser.TryParse(ev, out var te, out var error))
                parsed.Add(te!);
            else
                logger.LogWarning("Skipped event {Slug}: {Error}", ev.Slug, error);
        }

        logger.LogInformation(
            "Fetched {Total} temperature events, parsed {Parsed} ({Max} max / {Min} min), {Buckets} buckets, unknown source: {Unknown}",
            events.Count,
            parsed.Count,
            parsed.Count(e => e.Kind == TemperatureKind.Max),
            parsed.Count(e => e.Kind == TemperatureKind.Min),
            parsed.Sum(e => e.Buckets.Count),
            parsed.Count(e => e.SourceKind == ResolutionSourceKind.Unknown));
    }
}
