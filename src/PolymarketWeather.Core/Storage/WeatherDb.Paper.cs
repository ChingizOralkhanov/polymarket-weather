using Microsoft.EntityFrameworkCore;
using PolymarketWeather.Core.Markets;

namespace PolymarketWeather.Core.Storage;

public sealed record WbBucket(string MarketId, string Label, int Upper, string YesTokenId, string NoTokenId);

/// <summary>
/// An open daily-max event whose station is watched by strategy W-B, with the buckets not signalled yet
/// and its lowest ("or below") bucket, signalled or not.
/// </summary>
public sealed record WbCandidate(
    string EventId,
    string City,
    string StationId,
    DateOnly Date,
    TemperatureUnit Unit,
    string TimeZone,
    WbBucket? Lowest,
    IReadOnlyList<WbBucket> Buckets);

public sealed record LiveTemperature(long Ts, double TempC, DateTimeOffset? ReceivedAt);

/// <summary>Paper trading tables.</summary>
public sealed partial class WeatherDb
{
    public async Task<IReadOnlyList<WbCandidate>> GetWbCandidatesAsync(DateOnly todayUtc, CancellationToken ct = default)
    {
        var firstDay = todayUtc.AddDays(-1);
        var lastDay = todayUtc.AddDays(1);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await (
                from e in db.Events
                join s in db.Stations on e.StationId equals s.StationId
                join b in db.Buckets on e.EventId equals b.EventId
                where !e.Closed && e.Kind == TemperatureKind.Max && e.Date >= firstDay && e.Date <= lastDay
                      && ResolutionSources.MetarBased.Contains(e.SourceKind)
                      && b.Upper != null
                select new
                {
                    e.EventId, e.City, s.StationId, e.Date, e.Unit, s.TimeZone,
                    b.MarketId, b.Label, Upper = b.Upper!.Value, IsLowest = b.Lower == null, b.YesTokenId, b.NoTokenId,
                    Signalled = db.WbSignals.Any(w => w.MarketId == b.MarketId),
                })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.EventId)
            .Where(g => g.Any(b => !b.Signalled))
            .Select(g =>
            {
                var e = g.First();
                var lowest = g.FirstOrDefault(b => b.IsLowest);
                return new WbCandidate(e.EventId, e.City, e.StationId, e.Date, e.Unit, e.TimeZone,
                    lowest is null ? null : new WbBucket(lowest.MarketId, lowest.Label, lowest.Upper, lowest.YesTokenId, lowest.NoTokenId),
                    g.Where(b => !b.Signalled).Select(b => new WbBucket(b.MarketId, b.Label, b.Upper, b.YesTokenId, b.NoTokenId)).ToList());
            })
            .ToList();
    }

    /// <summary>Routine METAR temperatures of a station in a time window, oldest first.</summary>
    public async Task<IReadOnlyList<LiveTemperature>> GetLiveTemperaturesAsync(string stationId, long fromTs, long toTs, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.MetarLive
            // Special reports (SPECI) do not count towards the resolution value.
            .Where(m => m.StationId == stationId && m.Ts >= fromTs && m.Ts < toTs && m.TempC != null
                        && (m.Raw == null || !m.Raw.StartsWith("SPECI")))
            .OrderBy(m => m.Ts)
            .Select(m => new { m.Ts, m.TempC, m.ReceivedAt })
            .ToListAsync(ct);
        return rows.Select(r => new LiveTemperature(r.Ts, r.TempC!.Value, r.ReceivedAt)).ToList();
    }

    public async Task InsertWbSignalsAsync(IReadOnlyCollection<WbSignalRow> signals, CancellationToken ct = default)
    {
        if (signals.Count == 0)
            return;

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await InsertNewAsync(db, db.WbSignals, signals.ToList(), r => r.MarketId, ids => r => ids.Contains(r.MarketId), ct);
        await db.SaveChangesAsync(ct);
    }
}
