using System.Globalization;
using Dapper;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Musk;
using PolymarketWeather.Core.Observations;

namespace PolymarketWeather.Core.Storage;

public sealed record BookSnapshot(
    string TokenId,
    string MarketId,
    long Ts,
    decimal? BestBid,
    decimal? BestAsk,
    decimal? BidSize,
    decimal? AskSize,
    string Bids,
    string Asks);

public sealed record BookToken(string TokenId, string MarketId);

/// <summary>Tables written by the live collectors: post-count markets, posts, order books, live METARs.</summary>
public sealed partial class WeatherDb
{
    private const string LiveSchema = """
        CREATE TABLE IF NOT EXISTS count_events (
            event_id          TEXT PRIMARY KEY,
            slug              TEXT NOT NULL UNIQUE,
            series_id         TEXT,
            title             TEXT NOT NULL,
            created_at        TEXT,
            end_date          TEXT,
            closed            INTEGER NOT NULL,
            volume            REAL,
            winning_market_id TEXT,
            description       TEXT,
            updated_at        TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS count_buckets (
            market_id    TEXT PRIMARY KEY,
            event_id     TEXT NOT NULL,
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
        CREATE INDEX IF NOT EXISTS ix_count_buckets_event ON count_buckets (event_id);

        -- Counting windows of xtracker; market_slug links a window to its count_events row.
        CREATE TABLE IF NOT EXISTS trackings (
            tracking_id  TEXT PRIMARY KEY,
            handle       TEXT NOT NULL,
            title        TEXT NOT NULL,
            period_start TEXT NOT NULL,
            period_end   TEXT NOT NULL,
            market_slug  TEXT,
            is_active    INTEGER NOT NULL
        );

        -- collected_at is when we saw the post, to compare with imported_at (when xtracker counted it).
        CREATE TABLE IF NOT EXISTS posts (
            platform_id  TEXT PRIMARY KEY,
            handle       TEXT NOT NULL,
            created_at   TEXT NOT NULL,
            imported_at  TEXT,
            collected_at TEXT NOT NULL,
            content      TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_posts_handle_created ON posts (handle, created_at);

        -- Top of book per outcome token, written only when it changes. bids/asks: "price:size" pairs, best first.
        CREATE TABLE IF NOT EXISTS book_snapshots (
            token_id  TEXT NOT NULL,
            ts        INTEGER NOT NULL,
            market_id TEXT NOT NULL,
            best_bid  REAL,
            best_ask  REAL,
            bid_size  REAL,
            ask_size  REAL,
            bids      TEXT NOT NULL,
            asks      TEXT NOT NULL,
            PRIMARY KEY (token_id, ts)
        );

        CREATE TABLE IF NOT EXISTS metar_live (
            station_id   TEXT NOT NULL,
            ts           INTEGER NOT NULL,
            received_at  TEXT,
            collected_at TEXT NOT NULL,
            temp_c       REAL,
            raw          TEXT,
            PRIMARY KEY (station_id, ts)
        );
        """;

    public async Task UpsertCountEventsAsync(IEnumerable<CountEvent> events, CancellationToken ct = default)
    {
        const string eventSql = """
            INSERT INTO count_events (event_id, slug, series_id, title, created_at, end_date, closed, volume,
                                      winning_market_id, description, updated_at)
            VALUES (@EventId, @Slug, @SeriesId, @Title, @CreatedAt, @EndDate, @Closed, @Volume,
                    @WinningMarketId, @Description, @UpdatedAt)
            ON CONFLICT (event_id) DO UPDATE SET
                slug = excluded.slug, series_id = excluded.series_id, title = excluded.title,
                created_at = excluded.created_at, end_date = excluded.end_date, closed = excluded.closed,
                volume = excluded.volume, winning_market_id = excluded.winning_market_id,
                description = excluded.description, updated_at = excluded.updated_at;
            """;
        const string bucketSql = """
            INSERT INTO count_buckets (market_id, event_id, position, condition_id, yes_token_id, no_token_id,
                                       label, lower, upper, outcome, closed_time)
            VALUES (@MarketId, @EventId, @Position, @ConditionId, @YesTokenId, @NoTokenId,
                    @Label, @Lower, @Upper, @Outcome, @ClosedTime)
            ON CONFLICT (market_id) DO UPDATE SET
                position = excluded.position, label = excluded.label, lower = excluded.lower,
                upper = excluded.upper, outcome = excluded.outcome, closed_time = excluded.closed_time;
            """;

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var e in events)
        {
            await conn.ExecuteAsync(new CommandDefinition(eventSql, new
            {
                e.EventId,
                e.Slug,
                e.SeriesId,
                e.Title,
                CreatedAt = e.CreatedAt?.ToString("O"),
                EndDate = e.EndDate?.ToString("O"),
                e.Closed,
                e.Volume,
                WinningMarketId = e.WinningBucket?.MarketId,
                e.Description,
                UpdatedAt = now,
            }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(bucketSql, e.Buckets.Select((b, i) => new
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
            }), tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Events still stored as open although their end date has passed, oldest first.</summary>
    public async Task<IReadOnlyList<string>> GetOpenEventSlugsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT slug FROM (
                SELECT slug, end_date FROM events WHERE closed = 0 AND end_date < @Now
                UNION ALL
                SELECT slug, end_date FROM count_events WHERE closed = 0 AND end_date < @Now
            ) ORDER BY end_date
            """;
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, new { Now = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpsertTrackingsAsync(IEnumerable<Tracking> trackings, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO trackings (tracking_id, handle, title, period_start, period_end, market_slug, is_active)
            VALUES (@Id, @Handle, @Title, @Start, @End, @MarketSlug, @IsActive)
            ON CONFLICT (tracking_id) DO UPDATE SET title = excluded.title, period_start = excluded.period_start,
                period_end = excluded.period_end, market_slug = excluded.market_slug, is_active = excluded.is_active;
            """;
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, trackings.Select(t => new
        {
            t.Id,
            t.Handle,
            t.Title,
            Start = t.Start.ToString("O"),
            End = t.End.ToString("O"),
            MarketSlug = t.MarketLink?.TrimEnd('/').Split('/').LastOrDefault(),
            t.IsActive,
        }), cancellationToken: ct));
    }

    public async Task<DateTimeOffset?> GetLastPostTimeAsync(string handle, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var value = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT MAX(created_at) FROM posts WHERE handle = @handle", new { handle }, cancellationToken: ct));
        return value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Stores posts not seen before; returns how many were new.</summary>
    public async Task<int> InsertPostsAsync(IEnumerable<TrackedPost> posts, DateTimeOffset collectedAt, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO posts (platform_id, handle, created_at, imported_at, collected_at, content)
            VALUES (@PlatformId, @Handle, @CreatedAt, @ImportedAt, @CollectedAt, @Content)
            ON CONFLICT (platform_id) DO NOTHING;
            """;
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, posts.Select(p => new
        {
            p.PlatformId,
            p.Handle,
            CreatedAt = p.CreatedAt.ToString("O"),
            ImportedAt = p.ImportedAt?.ToString("O"),
            CollectedAt = collectedAt.ToString("O"),
            p.Content,
        }), cancellationToken: ct));
    }

    /// <summary>
    /// Yes tokens worth watching: open daily-max weather markets from yesterday to two days ahead
    /// and every open post-count market.
    /// </summary>
    public async Task<IReadOnlyList<BookToken>> GetActiveBookTokensAsync(DateOnly todayUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT b.yes_token_id AS TokenId, b.market_id AS MarketId
            FROM buckets b JOIN events e ON e.event_id = b.event_id
            WHERE e.closed = 0 AND e.kind = 'Max' AND e.date BETWEEN @From AND @To
            UNION ALL
            SELECT b.yes_token_id, b.market_id
            FROM count_buckets b JOIN count_events e ON e.event_id = b.event_id
            WHERE e.closed = 0
            """;
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<BookToken>(new CommandDefinition(sql, new
        {
            From = todayUtc.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = todayUtc.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SaveBookSnapshotsAsync(IReadOnlyCollection<BookSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0)
            return;

        const string sql = """
            INSERT INTO book_snapshots (token_id, ts, market_id, best_bid, best_ask, bid_size, ask_size, bids, asks)
            VALUES (@TokenId, @Ts, @MarketId, @BestBid, @BestAsk, @BidSize, @AskSize, @Bids, @Asks)
            ON CONFLICT (token_id, ts) DO NOTHING;
            """;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, snapshots, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>METAR stations of open weather markets whose local day is around now.</summary>
    public async Task<IReadOnlyList<string>> GetLiveStationsAsync(DateOnly todayUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT DISTINCT station_id FROM events
            WHERE closed = 0 AND station_id IS NOT NULL
              AND source_kind IN ('NoaaTimeseries', 'WeatherUnderground')
              AND date BETWEEN @From AND @To
            ORDER BY station_id
            """;
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, new
        {
            From = todayUtc.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = todayUtc.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Stores METARs not seen before; returns how many were new.</summary>
    public async Task<int> InsertLiveMetarsAsync(IEnumerable<LiveMetar> metars, DateTimeOffset collectedAt, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO metar_live (station_id, ts, received_at, collected_at, temp_c, raw)
            VALUES (@StationId, @Ts, @ReceivedAt, @CollectedAt, @TempC, @Raw)
            ON CONFLICT (station_id, ts) DO NOTHING;
            """;
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, metars.Select(m => new
        {
            m.StationId,
            Ts = m.ObservedAt.ToUnixTimeSeconds(),
            ReceivedAt = m.ReceivedAt?.ToString("O"),
            CollectedAt = collectedAt.ToString("O"),
            m.TempC,
            m.Raw,
        }), cancellationToken: ct));
    }
}
