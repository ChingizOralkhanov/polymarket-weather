using Microsoft.EntityFrameworkCore;
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
    public async Task UpsertCountEventsAsync(IEnumerable<CountEvent> events, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var list = events.ToList();
        var eventRows = list.Select(e => new CountEventRow
        {
            EventId = e.EventId,
            Slug = e.Slug,
            SeriesId = e.SeriesId,
            Title = e.Title,
            CreatedAt = e.CreatedAt,
            EndDate = e.EndDate,
            Closed = e.Closed,
            Volume = (double?)e.Volume,
            WinningMarketId = e.WinningBucket?.MarketId,
            Description = e.Description,
            UpdatedAt = now,
        }).ToList();
        var bucketRows = list.SelectMany(e => e.Buckets.Select((b, i) => new CountBucketRow
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
        await UpsertAsync(db, db.CountEvents, eventRows, r => r.EventId, ids => r => ids.Contains(r.EventId), ct);
        await UpsertAsync(db, db.CountBuckets, bucketRows, r => r.MarketId, ids => r => ids.Contains(r.MarketId), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Events still stored as open although their end date has passed, oldest first.</summary>
    public async Task<IReadOnlyList<string>> GetOpenEventSlugsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var weather = await db.Events
            .Where(e => !e.Closed && e.EndDate < now)
            .Select(e => new { e.Slug, e.EndDate })
            .ToListAsync(ct);
        var counts = await db.CountEvents
            .Where(e => !e.Closed && e.EndDate < now)
            .Select(e => new { e.Slug, e.EndDate })
            .ToListAsync(ct);
        return weather.Concat(counts).OrderBy(e => e.EndDate).Select(e => e.Slug).ToList();
    }

    public async Task UpsertTrackingsAsync(IEnumerable<Tracking> trackings, CancellationToken ct = default)
    {
        var rows = trackings.Select(t => new TrackingRow
        {
            TrackingId = t.Id,
            Handle = t.Handle,
            Title = t.Title,
            PeriodStart = t.Start,
            PeriodEnd = t.End,
            MarketSlug = t.MarketLink?.TrimEnd('/').Split('/').LastOrDefault(),
            IsActive = t.IsActive,
        }).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.Trackings, rows, r => r.TrackingId, ids => r => ids.Contains(r.TrackingId), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLastPostTimeAsync(string handle, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Posts
            .Where(p => p.Handle == handle)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => (DateTimeOffset?)p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Stores posts not seen before; returns how many were new.</summary>
    public async Task<int> InsertPostsAsync(IEnumerable<TrackedPost> posts, DateTimeOffset collectedAt, CancellationToken ct = default)
    {
        var rows = posts.Select(p => new PostRow
        {
            PlatformId = p.PlatformId,
            Handle = p.Handle,
            CreatedAt = p.CreatedAt,
            ImportedAt = p.ImportedAt,
            CollectedAt = collectedAt,
            Content = p.Content,
        }).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var added = await InsertNewAsync(db, db.Posts, rows, r => r.PlatformId, ids => r => ids.Contains(r.PlatformId), ct);
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Yes tokens worth watching: open daily-max weather markets from yesterday to two days ahead
    /// and every open post-count market.
    /// </summary>
    public async Task<IReadOnlyList<BookToken>> GetActiveBookTokensAsync(DateOnly todayUtc, CancellationToken ct = default)
    {
        var firstDay = todayUtc.AddDays(-1);
        var lastDay = todayUtc.AddDays(2);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var weather = await (
                from b in db.Buckets
                join e in db.Events on b.EventId equals e.EventId
                where !e.Closed && e.Kind == TemperatureKind.Max && e.Date >= firstDay && e.Date <= lastDay
                select new BookToken(b.YesTokenId, b.MarketId))
            .ToListAsync(ct);
        var counts = await (
                from b in db.CountBuckets
                join e in db.CountEvents on b.EventId equals e.EventId
                where !e.Closed
                select new BookToken(b.YesTokenId, b.MarketId))
            .ToListAsync(ct);
        return [.. weather, .. counts];
    }

    public async Task SaveBookSnapshotsAsync(IReadOnlyCollection<BookSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0)
            return;

        var rows = snapshots.Select(s => new BookSnapshotRow
        {
            TokenId = s.TokenId,
            Ts = s.Ts,
            MarketId = s.MarketId,
            BestBid = (double?)s.BestBid,
            BestAsk = (double?)s.BestAsk,
            BidSize = (double?)s.BidSize,
            AskSize = (double?)s.AskSize,
            Bids = s.Bids,
            Asks = s.Asks,
        }).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        foreach (var token in rows.GroupBy(r => r.TokenId))
        {
            var tokenId = token.Key;
            await InsertNewAsync(db, db.BookSnapshots, token.ToList(), r => r.Ts,
                ts => r => r.TokenId == tokenId && ts.Contains(r.Ts), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>METAR stations of open weather markets whose local day is around now.</summary>
    public async Task<IReadOnlyList<string>> GetLiveStationsAsync(DateOnly todayUtc, CancellationToken ct = default)
    {
        var from = todayUtc.AddDays(-1);
        var to = todayUtc.AddDays(1);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Events
            .Where(e => !e.Closed && e.StationId != null
                        && (e.SourceKind == ResolutionSourceKind.NoaaTimeseries || e.SourceKind == ResolutionSourceKind.WeatherUnderground)
                        && e.Date >= from && e.Date <= to)
            .Select(e => e.StationId!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);
    }

    /// <summary>Stores METARs not seen before; returns how many were new.</summary>
    public async Task<int> InsertLiveMetarsAsync(IEnumerable<LiveMetar> metars, DateTimeOffset collectedAt, CancellationToken ct = default)
    {
        var rows = metars.Select(m => new MetarLiveRow
        {
            StationId = m.StationId,
            Ts = m.ObservedAt.ToUnixTimeSeconds(),
            ReceivedAt = m.ReceivedAt,
            CollectedAt = collectedAt,
            TempC = m.TempC,
            Raw = m.Raw,
        }).ToList();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var added = 0;
        foreach (var station in rows.GroupBy(r => r.StationId))
        {
            var stationId = station.Key;
            added += await InsertNewAsync(db, db.MetarLive, station.ToList(), r => r.Ts,
                ts => r => r.StationId == stationId && ts.Contains(r.Ts), ct);
        }
        await db.SaveChangesAsync(ct);
        return added;
    }
}
