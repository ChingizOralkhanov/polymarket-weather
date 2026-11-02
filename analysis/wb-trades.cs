#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

// Real executed trades around a METAR that killed a bucket: who took the dead bucket, how many seconds after
// publication, and how much it paid. A taker buying No below 99c or selling Yes above 1c is taking that money.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
using var c = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
var signals = c.Query<Sig>(@"
    select b.condition_id ConditionId, w.city City, w.label Label, w.metar_ts MetarTs, w.metar_received_at ReceivedAt
    from wb_signals w join buckets b on b.market_id = w.market_id
    where w.detected_at > '2026-10-28' and w.metar_received_at is not null
      and (julianday(w.detected_at) - julianday(w.metar_received_at)) * 1440 <= 10").ToList();
Console.WriteLine($"fresh dead-bucket signals since 2026-10-28: {signals.Count}");

var takes = new ConcurrentBag<Take>();
await Parallel.ForEachAsync(signals, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (s, ct) =>
{
    var published = DateTimeOffset.Parse(s.ReceivedAt).ToUnixTimeSeconds();
    for (var off = 0; off < 3000; off += 500)
    {
        string json;
        try { json = await http.GetStringAsync($"https://data-api.polymarket.com/trades?market={s.ConditionId}&limit=500&offset={off}&takerOnly=true", ct); }
        catch (HttpRequestException) { break; }
        var page = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
        foreach (var t in page)
        {
            var ts = t.GetProperty("timestamp").GetInt64();
            if (ts < s.MetarTs - 600 || ts > published + 900) continue;
            var outcome = t.GetProperty("outcome").GetString();
            var side = t.GetProperty("side").GetString();
            var price = t.GetProperty("price").GetDouble();
            var size = t.GetProperty("size").GetDouble();
            // Yes-equivalent price of a trade betting against the dead bucket.
            double? yes = (outcome, side) switch { ("No", "BUY") => 1 - price, ("Yes", "SELL") => price, _ => null };
            if (yes is null || yes < 0.01) continue;
            takes.Add(new Take(s, ts - published, ts - s.MetarTs, yes.Value, size, t.GetProperty("proxyWallet").GetString()!,
                t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        }
        if (page.Count < 500) break;
    }
});

var all = takes.ToList();
Console.WriteLine($"trades against a dead bucket at Yes-equivalent >= 1c, within the window: {all.Count}; signals touched: {all.Select(t => t.S).Distinct().Count()}");
double Profit(IEnumerable<Take> xs) => xs.Sum(t => t.Size * t.YesPrice);

Console.WriteLine("\nseconds relative to publication on aviationweather | trades | signals | shares | $ taken (gross) | avg Yes price");
(string, long, long)[] bins = [
    ("before the observation time", long.MinValue, long.MaxValue),
    ("after obs, before publication", long.MinValue, 0), ("0-10 s", 0, 10), ("10-30 s", 10, 30), ("30-60 s", 30, 60),
    ("1-2 min", 60, 120), ("2-5 min", 120, 300), ("5-15 min", 300, 901)];
foreach (var (name, lo, hi) in bins)
{
    var g = name.StartsWith("before")
        ? all.Where(t => t.SinceObs < 0).ToList()
        : all.Where(t => t.SinceObs >= 0 && t.SincePublished >= lo && t.SincePublished < hi).ToList();
    Console.WriteLine($"  {name,-30} | {g.Count,4} | {g.Select(t => t.S).Distinct().Count(),3} | {g.Sum(t => t.Size),8:N0} | ${Profit(g),7:N0} | {(g.Count > 0 ? g.Average(t => t.YesPrice) * 100 : 0),5:F1}c");
}

// Money reachable by someone who sees the report a given time after publication.
Console.WriteLine("\nwho took it (after the observation time):");
foreach (var g in all.Where(t => t.SinceObs >= 0).GroupBy(t => t.Wallet).OrderByDescending(g => Profit(g)).Take(10))
{
    var secs = g.Select(t => t.SincePublished).Order().ToList();
    Console.WriteLine($"  {(g.First().Name.Length > 0 ? g.First().Name : g.Key[..10]),-22} trades {g.Count(),3}  ${Profit(g),6:N0}  median {secs[secs.Count / 2],5} s after publication");
}

record Sig(string ConditionId, string City, string Label, long MetarTs, string ReceivedAt);
record Take(Sig S, long SincePublished, long SinceObs, double YesPrice, double Size, string Wallet, string Name);
