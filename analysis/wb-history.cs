#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

// W-B: on the resolution day, once the observed METAR max passes a bucket's upper bound the bucket is dead.
// How fast does its Yes price go to zero, and how much could be taken by buying No right after the METAR?
var db = args.Length > 0 ? args[0] : @"C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db";
var publishDelay = TimeSpan.FromMinutes(5);   // METAR is public a few minutes after its observation time
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var cache = Path.Combine(Path.GetTempPath(), "wb-cache");
Directory.CreateDirectory(cache);

using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
var events = c.Query<(string event_id, string slug, string city, string date, string unit, string station_id, string winning_market_id)>(@"
    select event_id, slug, city, date, unit, station_id, winning_market_id from events
    where kind = 'Max' and closed = 1 and winning_market_id is not null and station_id is not null
      and source_kind = 'NoaaTimeseries' and date between '2026-08-25' and '2026-10-09'").ToList();
var buckets = c.Query<(string market_id, string event_id, string yes_token_id, string label, long? lower, long? upper, string outcome)>(
    "select market_id, event_id, yes_token_id, label, lower, upper, outcome from buckets").ToLookup(b => b.event_id);
var tz = c.Query<(string station_id, string time_zone)>("select station_id, time_zone from stations")
    .ToDictionary(s => s.station_id, s => TimeZoneInfo.FindSystemTimeZoneById(s.time_zone));
var obsByStation = new Dictionary<string, List<(long ts, double? c, double? f)>>();

var kills = new List<Kill>();
foreach (var e in events)
{
    if (!tz.TryGetValue(e.station_id, out var zone)) continue;
    if (!obsByStation.TryGetValue(e.station_id, out var obs))
        obsByStation[e.station_id] = obs = c.Query<(long ts, double? temp_c, double? temp_f)>(
            "select ts, temp_c, temp_f from observations where station_id = @s order by ts", new { s = e.station_id }).ToList();

    var day = DateOnly.Parse(e.date);
    var dayObs = obs.Where(o => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(o.ts), zone).DateTime) == day)
        .Select(o => (o.ts, v: e.unit == "Fahrenheit" ? o.f : o.c)).Where(o => o.v is not null).ToList();
    if (dayObs.Count < 12) continue;

    var alive = buckets[e.event_id].Where(b => b.upper is not null).ToList();
    var runningMax = int.MinValue;
    foreach (var (ts, v) in dayObs)
    {
        var x = (int)Math.Round(v!.Value, MidpointRounding.AwayFromZero);
        if (x <= runningMax) continue;
        runningMax = x;
        foreach (var b in alive.Where(b => b.upper < runningMax).ToList())
        {
            alive.Remove(b);
            // Sanity: a killed bucket must not be the winner.
            kills.Add(new Kill(e.city, e.slug, b.label, b.yes_token_id, b.outcome == "Yes", DateTimeOffset.FromUnixTimeSeconds(ts), runningMax));
        }
    }
}
Console.WriteLine($"events {events.Count}, dead-bucket moments {kills.Count}, of which the 'dead' bucket actually won: {kills.Count(k => k.Won)}");

// Minute prices around each kill.
var prices = new ConcurrentDictionary<Kill, List<(long T, double P)>>();
var n = 0;
await Parallel.ForEachAsync(kills, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (k, ct) =>
{
    var from = k.At.AddMinutes(-60).ToUnixTimeSeconds();
    var to = k.At.AddMinutes(180).ToUnixTimeSeconds();
    var url = $"https://clob.polymarket.com/prices-history?market={k.Token}&startTs={from}&endTs={to}&fidelity=1";
    var file = Path.Combine(cache, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".json");
    string json;
    if (File.Exists(file)) json = await File.ReadAllTextAsync(file, ct);
    else
    {
        try { json = await http.GetStringAsync(url, ct); } catch { json = "{\"history\":[]}"; }
        await File.WriteAllTextAsync(file, json, ct);
    }
    prices[k] = JsonDocument.Parse(json).RootElement.GetProperty("history").EnumerateArray()
        .Select(p => (p.GetProperty("t").GetInt64(), p.GetProperty("p").GetDouble())).ToList();
    if (Interlocked.Increment(ref n) % 2000 == 0) Console.WriteLine($"  prices {n}/{kills.Count}");
});

double? At(List<(long T, double P)> h, DateTimeOffset t)
{
    var ts = t.ToUnixTimeSeconds();
    var last = h.LastOrDefault(p => p.T <= ts);
    return last == default ? null : last.P;
}
static double Fee(double p) => 0.05 * p * (1 - p);

var rows = kills.Select(k =>
{
    var h = prices[k];
    var avail = k.At + publishDelay;
    return (k, before: At(h, k.At.AddMinutes(-10)), p0: At(h, avail), p10: At(h, avail.AddMinutes(10)), p30: At(h, avail.AddMinutes(30)), p60: At(h, avail.AddMinutes(60)));
}).Where(r => r.before is not null && r.p0 is not null).ToList();

Console.WriteLine($"dead buckets with price data: {rows.Count}");
Console.WriteLine();
Console.WriteLine("Yes price of a dead bucket | 10 min before METAR | at publish (+5m) | +10m | +30m | +60m");
foreach (var thr in new[] { 0.01, 0.03, 0.05, 0.10 })
    Console.WriteLine($"  share >= {thr * 100,2:F0}c | {rows.Count(r => r.before >= thr) * 1.0 / rows.Count,8:P1} | {rows.Count(r => r.p0 >= thr) * 1.0 / rows.Count,8:P1} | {rows.Count(r => r.p10 >= thr) * 1.0 / rows.Count,6:P1} | {rows.Count(r => r.p30 >= thr) * 1.0 / rows.Count,6:P1} | {rows.Count(r => r.p60 >= thr) * 1.0 / rows.Count,6:P1}");

// Buying No at (1 - Yes price) right after the METAR is public: profit = Yes price - fee, per share.
var trades = rows.Where(r => r.p0 >= 0.01).ToList();
Console.WriteLine();
Console.WriteLine($"Opportunities with Yes >= 1c at publish: {trades.Count} of {rows.Count} ({trades.Count * 1.0 / rows.Count:P1}), "
                  + $"over {rows.Select(r => r.k.Slug).Distinct().Count()} events / {(new DateTime(2026, 10, 9) - new DateTime(2026, 8, 25)).Days + 1} days");
if (trades.Count > 0)
{
    var profit = trades.Sum(r => (r.k.Won ? -(1 - r.p0!.Value) : r.p0!.Value) - Fee(1 - r.p0!.Value));
    Console.WriteLine($"  of them the 'dead' bucket actually won (No lost): {trades.Count(r => r.k.Won)}");
    foreach (var w in trades.Where(r => r.k.Won)) Console.WriteLine($"    LOSS: {w.k.Slug} [{w.k.Label}] METAR max {w.k.Max} at {w.k.At:MM-dd HH:mm}Z, Yes at publish {w.p0 * 100:F1}c");
    foreach (var w in rows.Where(r => r.k.Won)) Console.WriteLine($"    dead-but-won: {w.k.Slug} [{w.k.Label}] METAR max {w.k.Max} at {w.k.At:MM-dd HH:mm}Z, Yes before {w.before * 100:F1}c, at publish {w.p0 * 100:F1}c");
    Console.WriteLine($"  avg Yes price there {trades.Average(r => r.p0!.Value) * 100:F1}c; profit per share after fee: avg {profit / trades.Count * 100:F2}c; per $1 staked {profit / trades.Sum(r => 1 - r.p0!.Value):P2}");
    Console.WriteLine($"  with 10 shares per opportunity: ${profit * 10:F0} total over the period");
}

Console.WriteLine();
Console.WriteLine("by city (opportunities >= 1c at publish / dead buckets):");
foreach (var g in rows.GroupBy(r => r.k.City).OrderByDescending(g => g.Count(r => r.p0 >= 0.01)).Take(15))
    Console.WriteLine($"  {g.Key,-14} {g.Count(r => r.p0 >= 0.01),4} / {g.Count(),4}  avg Yes at publish when >=1c: {(g.Any(r => r.p0 >= 0.01) ? g.Where(r => r.p0 >= 0.01).Average(r => r.p0!.Value) * 100 : 0):F1}c");

Console.WriteLine();
Console.WriteLine("largest leftovers (Yes price at publish):");
foreach (var r in rows.OrderByDescending(r => r.p0).Take(12))
    Console.WriteLine($"  {r.k.Slug} [{r.k.Label}] METAR max {r.k.Max} at {r.k.At:MM-dd HH:mm}Z: before {r.before * 100:F1}c, publish {r.p0 * 100:F1}c, +10m {r.p10 * 100:F1}c, +30m {r.p30 * 100:F1}c");

record Kill(string City, string Slug, string Label, string Token, bool Won, DateTimeOffset At, int Max);
