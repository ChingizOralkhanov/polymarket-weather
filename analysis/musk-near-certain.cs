#:property PublishAot=false
using System.Collections.Concurrent;
using System.Text.Json;

// How often did "near-certain" bets on the post-count markets lose?
// For every resolved bucket: the first moment its Yes price reached >= T (buy Yes) or <= 1-T (buy No),
// and whether that side won.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
var cache = Path.Combine(Path.GetTempPath(), "musk-cache");
Directory.CreateDirectory(cache);

async Task<string> Get(string url)
{
    var file = Path.Combine(cache, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".json");
    if (File.Exists(file)) return await File.ReadAllTextAsync(file);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var body = await http.GetStringAsync(url);
            await File.WriteAllTextAsync(file, body);
            return body;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest) { Console.WriteLine($"  400 for {url}"); return "{\"history\":[]}"; }
        catch when (attempt < 4) { await Task.Delay(2000 * attempt); }
    }
}

string[] seriesIds = ["10000", "10816", "10181"];
var buckets = new List<B>();
foreach (var sid in seriesIds)
for (var off = 0; ; off += 100)
{
    using var doc = JsonDocument.Parse(await Get($"https://gamma-api.polymarket.com/events?series_id={sid}&closed=true&limit=100&offset={off}&order=endDate&ascending=false"));
    var arr = doc.RootElement.EnumerateArray().ToList();
    foreach (var e in arr)
    {
        if (!e.TryGetProperty("endDate", out var endEl) || !e.TryGetProperty("createdAt", out var crEl)) continue;
        var end = endEl.GetDateTimeOffset();
        var created = crEl.GetDateTimeOffset();
        foreach (var mk in e.GetProperty("markets").EnumerateArray())
        {
            if (!mk.TryGetProperty("umaResolutionStatus", out var st) || st.GetString() != "resolved") continue;
            var tokens = JsonSerializer.Deserialize<List<string>>(mk.GetProperty("clobTokenIds").GetString() ?? "[]")!;
            var prices = JsonSerializer.Deserialize<List<string>>(mk.GetProperty("outcomePrices").GetString() ?? "[]")!;
            if (tokens.Count < 2 || prices.Count < 2 || prices[0] is not ("0" or "1")) continue;
            var label = mk.TryGetProperty("groupItemTitle", out var gt) ? gt.GetString() ?? "" : "";
            buckets.Add(new B(sid, e.GetProperty("slug").GetString()!, label, tokens[0], prices[0] == "1", created, end));
        }
    }
    if (arr.Count < 100) break;
}
Console.WriteLine($"resolved buckets: {buckets.Count} in {buckets.Select(b => b.Event).Distinct().Count()} events "
                  + $"({string.Join(", ", buckets.GroupBy(b => b.Series).Select(g => $"series {g.Key}: {g.Select(b => b.Event).Distinct().Count()}"))})");

// 5-minute price history over each event's life.
var hist = new ConcurrentDictionary<string, List<(long T, double P)>>();
var done = 0;
await Parallel.ForEachAsync(buckets, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (b, ct) =>
{
    var json = await Get($"https://clob.polymarket.com/prices-history?market={b.Token}&startTs={Max(b.Created.AddDays(-1), b.End.AddDays(-15)).ToUnixTimeSeconds()}&endTs={b.End.AddDays(1).ToUnixTimeSeconds()}&fidelity=5");
    hist[b.Token] = JsonDocument.Parse(json).RootElement.GetProperty("history").EnumerateArray()
        .Select(p => (p.GetProperty("t").GetInt64(), p.GetProperty("p").GetDouble())).ToList();
    if (Interlocked.Increment(ref done) % 1000 == 0) Console.WriteLine($"  history {done}/{buckets.Count}");
});

static double Fee(double p) => 0.05 * p * (1 - p);
static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
double[] thresholds = [0.99, 0.995, 0.999];
var failures = new List<string>();

Console.WriteLine();
Console.WriteLine("side | threshold | bets | lost | loss rate | break-even loss rate | P&L per $1 (buy once at threshold, after fee)");
foreach (var side in new[] { "Yes", "No" })
foreach (var t in thresholds)
{
    int bets = 0, lost = 0; double pnl = 0, staked = 0;
    foreach (var b in buckets)
    {
        var h = hist.GetValueOrDefault(b.Token);
        if (h is null || h.Count == 0) continue;
        // Only moments before the counting window closes are tradable.
        var cut = b.End.ToUnixTimeSeconds();
        var hit = side == "Yes"
            ? h.FirstOrDefault(p => p.T < cut && p.P >= t)
            : h.FirstOrDefault(p => p.T < cut && p.P <= 1 - t);
        if (hit == default) continue;

        var price = side == "Yes" ? hit.P : 1 - hit.P;
        var won = side == "Yes" ? b.Won : !b.Won;
        bets++;
        staked += price;
        pnl += (won ? 1 : 0) - price - Fee(price);
        if (!won)
        {
            lost++;
            if (t == 0.999 || side == "Yes")
                failures.Add($"  {side} >= {t}: {b.Event} [{b.Label}] at {DateTimeOffset.FromUnixTimeSeconds(hit.T):yyyy-MM-dd HH:mm} UTC, {(b.End - DateTimeOffset.FromUnixTimeSeconds(hit.T)).TotalHours:F1} h before end, price {price:F4}");
        }
    }
    Console.WriteLine($"{side,-3} | {t,5} | {bets,5} | {lost,4} | {(bets > 0 ? (double)lost / bets : 0),8:P2} | {1 - t,8:P2} | {(staked > 0 ? pnl / staked : 0),7:+0.00%;-0.00%}");
}

Console.WriteLine();
Console.WriteLine($"losing near-certain bets ({failures.Distinct().Count()}):");
foreach (var f in failures.Distinct().Take(40)) Console.WriteLine(f);

record B(string Series, string Event, string Label, string Token, bool Won, DateTimeOffset Created, DateTimeOffset End);
