#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

// W-B under the METAR rules: how long does a bucket killed by a new METAR keep a price after the report
// is published on aviationweather.gov? Minute prices around each fresh signal since the rules changed.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
using var c = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
var signals = c.Query<Sig>(@"
    select w.market_id MarketId, b.yes_token_id Token, w.city City, w.label Label, w.metar_ts MetarTs,
           w.metar_received_at ReceivedAt, w.detected_at DetectedAt
    from wb_signals w join buckets b on b.market_id = w.market_id
    where w.detected_at > '2026-10-28' and w.metar_received_at is not null
      and (julianday(w.detected_at) - julianday(w.metar_received_at)) * 1440 <= 10").ToList();
Console.WriteLine($"fresh signals since 2026-10-28 (detected within 10 min of publication): {signals.Count}");

var rows = new ConcurrentBag<(Sig S, double? Before, double? P0, double? P1, double? P2, double? P5, double? P10)>();
await Parallel.ForEachAsync(signals, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (s, ct) =>
{
    var published = DateTimeOffset.Parse(s.ReceivedAt);
    var url = $"https://clob.polymarket.com/prices-history?market={s.Token}&startTs={published.AddMinutes(-40).ToUnixTimeSeconds()}&endTs={published.AddMinutes(20).ToUnixTimeSeconds()}&fidelity=1";
    List<(long T, double P)> h;
    try
    {
        h = JsonDocument.Parse(await http.GetStringAsync(url, ct)).RootElement.GetProperty("history").EnumerateArray()
            .Select(p => (p.GetProperty("t").GetInt64(), p.GetProperty("p").GetDouble())).ToList();
    }
    catch (HttpRequestException) { return; }

    double? At(TimeSpan offset)
    {
        var ts = published.Add(offset).ToUnixTimeSeconds();
        var last = h.LastOrDefault(p => p.T <= ts);
        return last == default ? null : last.P;
    }
    rows.Add((s, At(TimeSpan.FromMinutes(-30)), At(TimeSpan.Zero), At(TimeSpan.FromMinutes(1)), At(TimeSpan.FromMinutes(2)), At(TimeSpan.FromMinutes(5)), At(TimeSpan.FromMinutes(10))));
});

var data = rows.Where(r => r.P0 is not null).ToList();
Console.WriteLine($"with price data: {data.Count}");
Console.WriteLine("\nshare of dead buckets with Yes at or above | 30 min before | at publication | +1m | +2m | +5m | +10m");
foreach (var t in new[] { 0.01, 0.03, 0.05, 0.10, 0.30 })
    Console.WriteLine($"  {t * 100,3:F0}c | {data.Count(r => r.Before >= t) * 100.0 / data.Count,6:F1}% | {data.Count(r => r.P0 >= t) * 100.0 / data.Count,6:F1}% | {data.Count(r => r.P1 >= t) * 100.0 / data.Count,6:F1}% | {data.Count(r => r.P2 >= t) * 100.0 / data.Count,6:F1}% | {data.Count(r => r.P5 >= t) * 100.0 / data.Count,6:F1}% | {data.Count(r => r.P10 >= t) * 100.0 / data.Count,6:F1}%");

Console.WriteLine("\nlargest prices left at publication:");
foreach (var r in data.OrderByDescending(r => r.P0).Take(10))
    Console.WriteLine($"  {r.S.City,-14} [{r.S.Label,-14}] 30m before {r.Before * 100,5:F1}c | publication {r.P0 * 100,5:F1}c | +1m {r.P1 * 100,5:F1}c | +2m {r.P2 * 100,5:F1}c | +5m {r.P5 * 100,5:F1}c | +10m {r.P10 * 100,5:F1}c");

record Sig(string MarketId, string Token, string City, string Label, long MetarTs, string ReceivedAt, string DetectedAt);
