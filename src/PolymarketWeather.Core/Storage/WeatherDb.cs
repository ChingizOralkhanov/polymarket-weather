using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Observations;

namespace PolymarketWeather.Core.Storage;

public static class WeatherDbServiceCollectionExtensions
{
    public static IServiceCollection AddWeatherDb(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<WeatherDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<WeatherDb>();
        return services;
    }
}

public sealed partial class WeatherDb(IDbContextFactory<WeatherDbContext> contextFactory)
{
    // Keeps "IN (...)" lists well below SQLite's bound-parameter limit.
    private const int KeyChunk = 500;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (db.Database.IsSqlite())
        {
            var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        await SchemaBaseline.ApplyAsync(db, ct);
        await db.Database.MigrateAsync(ct);

        // WAL lets several collectors write while analysis reads.
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }

    public async Task UpsertEventsAsync(IEnumerable<TemperatureEvent> events, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var list = events.ToList();
        var eventRows = list.Select(e => new EventRow
        {
            EventId = e.EventId,
            Slug = e.Slug,
            SeriesId = e.SeriesId,
            City = e.City,
            Kind = e.Kind,
            Date = e.Date,
            Unit = e.Unit,
            SourceKind = e.SourceKind,
            StationId = e.StationId,
            CreatedAt = e.CreatedAt,
            EndDate = e.EndDate,
            Closed = e.Closed,
            Volume = (double?)e.Volume,
            WinningMarketId = e.WinningBucket?.MarketId,
            Description = e.Description,
            UpdatedAt = now,
        }).ToList();
        var bucketRows = list.SelectMany(e => e.Buckets.Select((b, i) => new BucketRow
        {
            MarketId = b.MarketId,
            EventId = e.EventId,
            Position = i,
            ConditionId = b.ConditionId,
            YesTokenId = b.YesTokenId,
            NoTokenId = b.NoTokenId,
            Label = b.Label,
            Lower = b.Lower,
            Upper = b.Upper,
            Outcome = b.Outcome,
            ClosedTime = b.ClosedTime,
        })).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.Events, eventRows, r => r.EventId, ids => r => ids.Contains(r.EventId), ct);
        await UpsertAsync(db, db.Buckets, bucketRows, r => r.MarketId, ids => r => ids.Contains(r.MarketId), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertSkippedAsync(IEnumerable<(string Slug, string? EventId, string Error)> skipped, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = skipped.Select(s => new SkippedEventRow { Slug = s.Slug, EventId = s.EventId, Error = s.Error, UpdatedAt = now }).ToList();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.SkippedEvents, rows, r => r.Slug, ids => r => ids.Contains(r.Slug), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Resolved buckets of closed events whose price history has not been fetched yet.</summary>
    public async Task<IReadOnlyList<PendingPriceFetch>> GetPendingPriceFetchesAsync(
        TemperatureKind? kind, IReadOnlyCollection<string>? cities, CancellationToken ct = default)
    {
        var allCities = cities is null || cities.Count == 0;
        var cityList = cities?.ToList() ?? [];

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await (
                from b in db.Buckets
                join e in db.Events on b.EventId equals e.EventId
                where e.Closed && b.Outcome != BucketOutcome.Unresolved
                      && !db.PriceFetches.Any(f => f.MarketId == b.MarketId)
                      && (kind == null || e.Kind == kind)
                      && (allCities || cityList.Contains(e.City))
                orderby e.Date descending, b.MarketId
                select new { b.MarketId, b.YesTokenId, b.EventId, e.CreatedAt, e.Date })
            .ToListAsync(ct);

        // Latest bucket close per event: when the event's prices stop moving.
        var eventIds = rows.Select(r => r.EventId).Distinct().ToList();
        var closedTimes = new Dictionary<string, DateTimeOffset>();
        foreach (var chunk in eventIds.Chunk(KeyChunk))
        {
            var times = await db.Buckets
                .Where(b => chunk.Contains(b.EventId) && b.ClosedTime != null)
                .Select(b => new { b.EventId, b.ClosedTime })
                .ToListAsync(ct);
            foreach (var t in times)
                if (!closedTimes.TryGetValue(t.EventId, out var max) || t.ClosedTime > max)
                    closedTimes[t.EventId] = t.ClosedTime!.Value;
        }

        return rows.Select(r => new PendingPriceFetch(
                r.MarketId, r.YesTokenId, r.CreatedAt, r.Date,
                closedTimes.TryGetValue(r.EventId, out var closed) ? closed : null))
            .ToList();
    }

    public async Task SavePricesAsync(string marketId, int fidelity, IReadOnlyList<(long Ts, decimal Price)> points, CancellationToken ct = default)
    {
        var rows = points.Select(p => new PriceRow { MarketId = marketId, Ts = p.Ts, Price = (double)p.Price }).ToList();
        var fetch = new PriceFetchRow { MarketId = marketId, Fidelity = fidelity, Points = points.Count, FetchedAt = DateTimeOffset.UtcNow };

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.Prices, rows, r => r.Ts, ts => r => r.MarketId == marketId && ts.Contains(r.Ts), ct);
        await UpsertAsync(db, db.PriceFetches, [fetch], r => r.MarketId, ids => r => ids.Contains(r.MarketId), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>METAR stations used for resolution, with the date span their events cover.</summary>
    public async Task<IReadOnlyList<(string StationId, DateOnly From, DateOnly To)>> GetResolutionStationsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Events
            .Where(e => e.StationId != null
                        && (e.SourceKind == ResolutionSourceKind.NoaaTimeseries || e.SourceKind == ResolutionSourceKind.WeatherUnderground))
            .Select(e => new { StationId = e.StationId!, e.Date })
            .Distinct()
            .ToListAsync(ct);
        return rows.GroupBy(r => r.StationId)
            .Select(g => (g.Key, g.Min(r => r.Date), g.Max(r => r.Date)))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<bool> HasStationAsync(string stationId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Stations.AnyAsync(s => s.StationId == stationId, ct);
    }

    public async Task UpsertStationAsync(StationInfo s, CancellationToken ct = default)
    {
        var row = new StationRow
        {
            StationId = s.StationId,
            Name = s.Name,
            Latitude = s.Latitude,
            Longitude = s.Longitude,
            Elevation = s.Elevation,
            TimeZone = s.TimeZone,
        };
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.Stations, [row], r => r.StationId, ids => r => ids.Contains(r.StationId), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Latest stored observation time for a station, or null if none.</summary>
    public async Task<DateTimeOffset?> GetLastObservationAsync(string stationId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var ts = await db.Observations.Where(o => o.StationId == stationId).MaxAsync(o => (long?)o.Ts, ct);
        return ts is null ? null : DateTimeOffset.FromUnixTimeSeconds(ts.Value);
    }

    public async Task SaveObservationsAsync(string stationId, IReadOnlyList<MetarObservation> observations, CancellationToken ct = default)
    {
        var rows = observations.Select(o => new ObservationRow
        {
            StationId = stationId,
            Ts = o.Time.ToUnixTimeSeconds(),
            TempC = o.TempC,
            TempF = o.TempF,
            Metar = o.Metar,
        }).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.Observations, rows, r => r.Ts, ts => r => r.StationId == stationId && ts.Contains(r.Ts), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StationInfo>> GetStationsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Stations
            .OrderBy(s => s.StationId)
            .Select(s => new StationInfo(s.StationId, s.Name, s.Latitude, s.Longitude, s.Elevation, s.TimeZone))
            .ToListAsync(ct);
    }

    public async Task<DateOnly?> GetLastForecastDateAsync(string stationId, string model, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ForecastDaily
            .Where(f => f.StationId == stationId && f.Model == model)
            .OrderByDescending(f => f.Date)
            .Select(f => (DateOnly?)f.Date)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveForecastDailyAsync(IEnumerable<ForecastDaily> forecasts, CancellationToken ct = default)
    {
        var rows = forecasts.Select(f => new ForecastDailyRow
        {
            StationId = f.StationId,
            Model = f.Model,
            LeadDays = f.LeadDays,
            Date = f.Date,
            TmaxC = f.TmaxC,
            TminC = f.TminC,
            Hours = f.Hours,
        }).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        foreach (var group in rows.GroupBy(r => (r.StationId, r.Model)))
        {
            var (stationId, model) = group.Key;
            await UpsertAsync(db, db.ForecastDaily, group.ToList(), r => (r.LeadDays, r.Date),
                keys =>
                {
                    var dates = keys.Select(k => k.Date).Distinct().ToList();
                    return r => r.StationId == stationId && r.Model == model && dates.Contains(r.Date);
                }, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds rows whose key is new and overwrites the stored values of the rest.
    /// <paramref name="existing"/> builds a filter that loads at least the stored rows among the given keys.
    /// </summary>
    private static Task UpsertAsync<TRow, TKey>(
        WeatherDbContext db, DbSet<TRow> set, IReadOnlyList<TRow> rows, Func<TRow, TKey> key,
        Func<List<TKey>, Expression<Func<TRow, bool>>> existing, CancellationToken ct)
        where TRow : class where TKey : notnull =>
        MergeAsync(db, set, rows, key, existing, overwrite: true, ct);

    /// <summary>Adds rows whose key is not stored yet; returns how many were added.</summary>
    private static Task<int> InsertNewAsync<TRow, TKey>(
        WeatherDbContext db, DbSet<TRow> set, IReadOnlyList<TRow> rows, Func<TRow, TKey> key,
        Func<List<TKey>, Expression<Func<TRow, bool>>> existing, CancellationToken ct)
        where TRow : class where TKey : notnull =>
        MergeAsync(db, set, rows, key, existing, overwrite: false, ct);

    private static async Task<int> MergeAsync<TRow, TKey>(
        WeatherDbContext db, DbSet<TRow> set, IReadOnlyList<TRow> rows, Func<TRow, TKey> key,
        Func<List<TKey>, Expression<Func<TRow, bool>>> existing, bool overwrite, CancellationToken ct)
        where TRow : class where TKey : notnull
    {
        // Last row wins for duplicate keys within one batch, as with repeated INSERT ... ON CONFLICT.
        var latest = new Dictionary<TKey, TRow>();
        foreach (var row in rows)
            latest[key(row)] = row;

        var added = 0;
        foreach (var chunk in latest.Keys.Chunk(KeyChunk))
        {
            var stored = (await set.Where(existing(chunk.ToList())).ToListAsync(ct)).ToDictionary(key);
            foreach (var k in chunk)
            {
                if (stored.TryGetValue(k, out var current))
                {
                    if (overwrite)
                        db.Entry(current).CurrentValues.SetValues(latest[k]);
                }
                else
                {
                    set.Add(latest[k]);
                    added++;
                }
            }
        }
        return added;
    }
}
