namespace PolymarketWeather.Collector;

/// <summary>Runs a cycle on a fixed interval; a failed cycle is logged and the next one runs as scheduled.</summary>
public abstract class PeriodicWorker(ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "{Worker} cycle failed", GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
