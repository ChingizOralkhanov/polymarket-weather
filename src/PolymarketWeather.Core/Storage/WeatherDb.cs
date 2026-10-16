using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Observations;

namespace PolymarketWeather.Core.Storage;

public sealed partial class WeatherDb(string connectionString)
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            event_id          TEXT PRIMARY KEY,
            slug              TEXT NOT NULL UNIQUE,
            series_id         TEXT,
            city              TEXT NOT NULL,
            kind              TEXT NOT NULL,
            date              TEXT NOT NULL,
            unit              TEXT NOT NULL,
            source_kind       TEXT NOT NULL,
            station_id        TEXT,
            created_at        TEXT,
            end_date          TEXT,
            closed            INTEGER NOT NULL,
            volume            REAL,
            winning_market_id TEXT,
            description       TEXT,
            updated_at        TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_city_kind_date ON events (city, kind, date);

        CREATE TABLE IF NOT EXISTS buckets (
            market_id    TEXT PRIMARY KEY,
            event_id     TEXT NOT NULL REFERENCES events (event_id),
            position     INTEGER NOT NULL,
            condition_id TEXT NOT NULL,
            yes_token_id TEXT NOT NULL,
            no_token_id  TEXT NOT NULL,
            label        TEXT NOT NULL,
            lower        INTEGER,
            upper        INTEGER,
            outcome      TEXT NOT NULL,
            closed_time  TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_buckets_event ON buckets (event_id);

        -- Yes-token price; the No token is its complement.
        CREATE TABLE IF NOT EXISTS prices (
            market_id TEXT NOT NULL,
            ts        INTEGER NOT NULL,
            price     REAL NOT NULL,
            PRIMARY KEY (market_id, ts)
        );

        CREATE TABLE IF NOT EXISTS price_fetches (
            market_id  TEXT PRIMARY KEY,
            fidelity   INTEGER NOT NULL,
            points     INTEGER NOT NULL,
            fetched_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS stations (
            station_id TEXT PRIMARY KEY,
            name       TEXT NOT NULL,
            latitude   REAL NOT NULL,
            longitude  REAL NOT NULL,
            elevation  REAL,
            time_zone  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS observations (
            station_id TEXT NOT NULL,
            ts         INTEGER NOT NULL,
            temp_c     REAL,
            temp_f     REAL,
            metar      TEXT,
            PRIMARY KEY (station_id, ts)
        );

        -- Daily extremes over the station's local day, from hourly values of a model run made lead_days earlier.
        CREATE TABLE IF NOT EXISTS forecast_daily (
            station_id TEXT NOT NULL,
            model      TEXT NOT NULL,
            lead_days  INTEGER NOT NULL,
            date       TEXT NOT NULL,
            tmax_c     REAL,
            tmin_c     REAL,
            hours      INTEGER NOT NULL,
            PRIMARY KEY (station_id, model, lead_days, date)
        );

        CREATE TABLE IF NOT EXISTS skipped_events (
            slug       TEXT PRIMARY KEY,
            event_id   TEXT,
            error      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private const string UpsertEventSql = """
        INSERT INTO events (event_id, slug, series_id, city, kind, date, unit, source_kind, station_id,
                            created_at, end_date, closed, volume, winning_market_id, description, updated_at)
        VALUES (@EventId, @Slug, @SeriesId, @City, @Kind, @Date, @Unit, @SourceKind, @StationId,
                @CreatedAt, @EndDate, @Closed, @Volume, @WinningMarketId, @Description, @UpdatedAt)
        ON CONFLICT (event_id) DO UPDATE SET
            slug = excluded.slug, series_id = excluded.series_id, city = excluded.city, kind = excluded.kind,
            date = excluded.date, unit = excluded.unit, source_kind = excluded.source_kind,
            station_id = excluded.station_id, created_at = excluded.created_at, end_date = excluded.end_date,
            closed = excluded.closed, volume = excluded.volume, winning_market_id = excluded.winning_market_id,
            description = excluded.description, updated_at = excluded.updated_at;
        """;

    private const string UpsertBucketSql = """
        INSERT INTO buckets (market_id, event_id, position, condition_id, yes_token_id, no_token_id,
                             label, lower, upper, outcome, closed_time)
        VALUES (@MarketId, @EventId, @Position, @ConditionId, @YesTokenId, @NoTokenId,
                @Label, @Lower, @Upper, @Outcome, @ClosedTime)
        ON CONFLICT (market_id) DO UPDATE SET
            event_id = excluded.event_id, position = excluded.position, label = excluded.label,
            lower = excluded.lower, upper = excluded.upper, outcome = excluded.outcome,
            closed_time = excluded.closed_time;
        """;

    private const string UpsertSkippedSql = """
        INSERT INTO skipped_events (slug, event_id, error, updated_at)
        VALUES (@Slug, @EventId, @Error, @UpdatedAt)
        ON CONFLICT (slug) DO UPDATE SET event_id = excluded.event_id, error = excluded.error, updated_at = excluded.updated_at;
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // WAL lets several collectors write while analysis reads.
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(Schema, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(LiveSchema, cancellationToken: ct));
    }

    public async Task UpsertEventsAsync(IEnumerable<TemperatureEvent> events, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var e in events)
        {
            await conn.ExecuteAsync(new CommandDefinition(UpsertEventSql, new
            {
                e.EventId,
                e.Slug,
                e.SeriesId,
                e.City,
                Kind = e.Kind.ToString(),
                Date = e.Date.ToString("yyyy-MM-dd"),
                Unit = e.Unit.ToString(),
                SourceKind = e.SourceKind.ToString(),
                e.StationId,
                CreatedAt = e.CreatedAt?.ToString("O"),
                EndDate = e.EndDate?.ToString("O"),
                e.Closed,
                e.Volume,
                WinningMarketId = e.WinningBucket?.MarketId,
                e.Description,
                UpdatedAt = now,
            }, tx, cancellationToken: ct));

            var buckets = e.Buckets.Select((b, i) => new
            {
                b.MarketId,
                e.EventId,
                Position = i,
                b.ConditionId,
                b.YesTokenId,
                b.NoTokenId,
                b.Label,
                b.Lower,
                b.Upper,
                Outcome = b.Outcome.ToString(),
                ClosedTime = b.ClosedTime?.ToString("O"),
            });
            await conn.ExecuteAsync(new CommandDefinition(UpsertBucketSql, buckets, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task UpsertSkippedAsync(IEnumerable<(string Slug, string? EventId, string Error)> skipped, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var conn = await OpenAsync(ct);
        var rows = skipped.Select(s => new { s.Slug, s.EventId, s.Error, UpdatedAt = now });
        await conn.ExecuteAsync(new CommandDefinition(UpsertSkippedSql, rows, cancellationToken: ct));
    }

    /// <summary>Resolved buckets of closed events whose price history has not been fetched yet.</summary>
    public async Task<IReadOnlyList<PendingPriceFetch>> GetPendingPriceFetchesAsync(
        string? kind, IReadOnlyCollection<string>? cities, CancellationToken ct = default)
    {
        const string sql = """
            SELECT b.market_id AS MarketId, b.yes_token_id AS YesTokenId,
                   e.created_at AS CreatedAt, e.date AS Date,
                   (SELECT MAX(b2.closed_time) FROM buckets b2 WHERE b2.event_id = e.event_id) AS ClosedTime
            FROM buckets b
            JOIN events e ON e.event_id = b.event_id
            LEFT JOIN price_fetches f ON f.market_id = b.market_id
            WHERE e.closed = 1 AND b.outcome <> 'Unresolved' AND f.market_id IS NULL
              AND (@Kind IS NULL OR e.kind = @Kind)
              AND (@AllCities = 1 OR e.city IN @Cities)
            ORDER BY e.date DESC, b.market_id
            """;
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<PendingPriceFetch>(new CommandDefinition(sql, new
        {
            Kind = kind,
            AllCities = cities is null || cities.Count == 0,
            Cities = cities ?? [],
        }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SavePricesAsync(string marketId, int fidelity, IReadOnlyList<(long Ts, decimal Price)> points, CancellationToken ct = default)
    {
        const string insertPrice = """
            INSERT INTO prices (market_id, ts, price) VALUES (@MarketId, @Ts, @Price)
            ON CONFLICT (market_id, ts) DO UPDATE SET price = excluded.price;
            """;
        const string insertFetch = """
            INSERT INTO price_fetches (market_id, fidelity, points, fetched_at)
            VALUES (@MarketId, @Fidelity, @Points, @FetchedAt)
            ON CONFLICT (market_id) DO UPDATE SET fidelity = excluded.fidelity, points = excluded.points, fetched_at = excluded.fetched_at;
            """;

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            insertPrice, points.Select(p => new { MarketId = marketId, p.Ts, p.Price }), tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertFetch, new
        {
            MarketId = marketId,
            Fidelity = fidelity,
            Points = points.Count,
            FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>METAR stations used for resolution, with the date span their events cover.</summary>
    public async Task<IReadOnlyList<(string StationId, DateOnly From, DateOnly To)>> GetResolutionStationsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT station_id AS StationId, MIN(date) AS FromDate, MAX(date) AS ToDate
            FROM events
            WHERE station_id IS NOT NULL AND source_kind IN ('NoaaTimeseries', 'WeatherUnderground')
            GROUP BY station_id
            ORDER BY station_id
            """;
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<(string StationId, string FromDate, string ToDate)>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => (r.StationId, DateOnly.Parse(r.FromDate), DateOnly.Parse(r.ToDate))).ToList();
    }

    public async Task<bool> HasStationAsync(string stationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM stations WHERE station_id = @stationId", new { stationId }, cancellationToken: ct)) > 0;
    }

    public async Task UpsertStationAsync(StationInfo s, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO stations (station_id, name, latitude, longitude, elevation, time_zone)
            VALUES (@StationId, @Name, @Latitude, @Longitude, @Elevation, @TimeZone)
            ON CONFLICT (station_id) DO UPDATE SET name = excluded.name, latitude = excluded.latitude,
                longitude = excluded.longitude, elevation = excluded.elevation, time_zone = excluded.time_zone;
            """;
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, s, cancellationToken: ct));
    }

    /// <summary>Latest stored observation time for a station, or null if none.</summary>
    public async Task<DateTimeOffset?> GetLastObservationAsync(string stationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var ts = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT MAX(ts) FROM observations WHERE station_id = @stationId", new { stationId }, cancellationToken: ct));
        return ts is null ? null : DateTimeOffset.FromUnixTimeSeconds(ts.Value);
    }

    public async Task SaveObservationsAsync(string stationId, IReadOnlyList<MetarObservation> observations, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO observations (station_id, ts, temp_c, temp_f, metar)
            VALUES (@StationId, @Ts, @TempC, @TempF, @Metar)
            ON CONFLICT (station_id, ts) DO UPDATE SET temp_c = excluded.temp_c, temp_f = excluded.temp_f, metar = excluded.metar;
            """;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, observations.Select(o => new
        {
            StationId = stationId,
            Ts = o.Time.ToUnixTimeSeconds(),
            o.TempC,
            o.TempF,
            o.Metar,
        }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<StationInfo>> GetStationsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT station_id AS StationId, name AS Name, latitude AS Latitude, longitude AS Longitude,
                   elevation AS Elevation, time_zone AS TimeZone
            FROM stations ORDER BY station_id
            """;
        await using var conn = await OpenAsync(ct);
        return (await conn.QueryAsync<StationInfo>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<DateOnly?> GetLastForecastDateAsync(string stationId, string model, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var date = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT MAX(date) FROM forecast_daily WHERE station_id = @stationId AND model = @model",
            new { stationId, model }, cancellationToken: ct));
        return date is null ? null : DateOnly.Parse(date, CultureInfo.InvariantCulture);
    }

    public async Task SaveForecastDailyAsync(IEnumerable<ForecastDaily> rows, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO forecast_daily (station_id, model, lead_days, date, tmax_c, tmin_c, hours)
            VALUES (@StationId, @Model, @LeadDays, @Date, @TmaxC, @TminC, @Hours)
            ON CONFLICT (station_id, model, lead_days, date) DO UPDATE SET
                tmax_c = excluded.tmax_c, tmin_c = excluded.tmin_c, hours = excluded.hours;
            """;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, rows.Select(r => new
        {
            r.StationId,
            r.Model,
            r.LeadDays,
            Date = r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.TmaxC,
            r.TminC,
            r.Hours,
        }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
