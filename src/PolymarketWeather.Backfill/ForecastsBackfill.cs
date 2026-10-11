using System.Net;
using PolymarketWeather.Core.Forecasts;
using PolymarketWeather.Core.Observations;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Backfill;

/// <summary>
/// Loads archived model forecasts (Open-Meteo Previous Runs) for every resolution station
/// and stores daily max/min over the station's local day for each lead time.
/// Requires the stations table, i.e. run "observations" first.
/// </summary>
public sealed class ForecastsBackfill(OpenMeteoClient openMeteo, WeatherDb db, ILogger<ForecastsBackfill> logger)
{
    private static readonly int[] Leads = [0, 1, 2, 3];

    private static readonly string[] GlobalModels =
    [
        "ecmwf_ifs025", "ecmwf_aifs025_single", "gfs_global", "icon_seamless", "gem_global",
        "jma_gsm", "meteofrance_seamless", "ukmo_seamless", "kma_seamless", "cma_grapes_global",
    ];

    private static readonly string[] UsModels = ["ncep_hrrr_conus", "ncep_nbm_conus"];

    // A multi-month, multi-variable request costs many API "calls"; stay well under the hourly quota.
    private static readonly TimeSpan RequestDelay = TimeSpan.FromSeconds(6);

    public async Task RunAsync(IReadOnlyCollection<string> stationFilter, CancellationToken ct)
    {
        await db.EnsureSchemaAsync(ct);
        var spans = (await db.GetResolutionStationsAsync(ct)).ToDictionary(s => s.StationId);
        var stations = (await db.GetStationsAsync(ct))
            .Where(s => spans.ContainsKey(s.StationId))
            .Where(s => stationFilter.Count == 0 || stationFilter.Contains(s.StationId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        logger.LogInformation("{Count} stations", stations.Count);

        foreach (var station in stations)
        {
            var (_, from, to) = spans[station.StationId];
            to = Min(to, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
            var tz = TimeZoneInfo.FindSystemTimeZoneById(station.TimeZone);
            var models = station.StationId.StartsWith('K') ? GlobalModels.Concat(UsModels) : GlobalModels;

            foreach (var model in models)
            {
                var last = await db.GetLastForecastDateAsync(station.StationId, model, ct);
                var start = last?.AddDays(1) ?? from;
                if (start > to)
                    continue;

                var hourly = await FetchWithRetryAsync(station, model, start.AddDays(-1), to.AddDays(1), ct);
                if (hourly is null)
                    continue;
                var rows = Aggregate(station.StationId, model, tz, hourly, start, to);
                await db.SaveForecastDailyAsync(rows, ct);
                logger.LogInformation("{Station} {Model}: {Rows} daily rows {From}..{To}", station.StationId, model, rows.Count, start, to);
                await Task.Delay(RequestDelay, ct);
            }
        }

        logger.LogInformation("Done");
    }

    /// <summary>Retries transient failures with growing pauses; returns null when the model is skipped for this station.</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<HourlyForecast>>?> FetchWithRetryAsync(
        StationInfo station, string model, DateOnly from, DateOnly to, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await openMeteo.GetPreviousRunsAsync(station.Latitude, station.Longitude, model, Leads, from, to, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                logger.LogWarning("{Station} {Model}: skipped, {Message}", station.StationId, model, ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt == maxAttempts)
                {
                    logger.LogWarning("{Station} {Model}: skipped after {Attempts} attempts, {Message}", station.StationId, model, attempt, ex.Message);
                    return null;
                }
                logger.LogWarning("{Station} {Model}: attempt {Attempt} failed ({Message}), waiting", station.StationId, model, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromMinutes(attempt * 2), ct);
            }
        }
    }

    private static List<ForecastDaily> Aggregate(
        string stationId, string model, TimeZoneInfo tz,
        IReadOnlyDictionary<int, IReadOnlyList<HourlyForecast>> hourly, DateOnly from, DateOnly to)
    {
        var rows = new List<ForecastDaily>();
        foreach (var (lead, series) in hourly)
        {
            var byDay = series
                .Where(h => h.TempC is not null)
                .GroupBy(h => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(h.Time, tz).DateTime))
                .Where(g => g.Key >= from && g.Key <= to);

            foreach (var day in byDay)
                rows.Add(new ForecastDaily(stationId, model, lead, day.Key, day.Max(h => h.TempC), day.Min(h => h.TempC), day.Count()));
        }
        return rows;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
