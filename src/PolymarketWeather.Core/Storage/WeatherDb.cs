using Dapper;
using Microsoft.Data.Sqlite;
using PolymarketWeather.Core.Markets;

namespace PolymarketWeather.Core.Storage;

public sealed class WeatherDb(string connectionString)
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
        await conn.ExecuteAsync(new CommandDefinition(Schema, cancellationToken: ct));
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
