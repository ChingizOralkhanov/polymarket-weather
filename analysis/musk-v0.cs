#:property PublishAot=false
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// v0 for the weekly post-count markets:
//  1) does our count from xtracker posts reproduce the winning bucket;
//  2) M-D: buy the favourite at 90-99c near the end — calibration and P&L after fees;
//  3) M-B: a simple pace model vs the market price (log-loss), walk-forward.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
var cache = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "cache");
Directory.CreateDirectory(cache);

async Task<string> Get(string url)
{
    var file = Path.Combine(cache, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".json");
    if (File.Exists(file)) return await File.ReadAllTextAsync(file);
    var body = await http.GetStringAsync(url);
    await File.WriteAllTextAsync(file, body);
    return body;
}

// Posts (true publication times).
var postTimes = new List<DateTimeOffset>();
for (var d = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero); d < DateTimeOffset.UtcNow.AddDays(-7); d = d.AddDays(7))
{
    using var doc = JsonDocument.Parse(await Get($"https://xtracker.polymarket.com/api/users/elonmusk/posts?startDate={d:yyyy-MM-ddTHH:mm:ssZ}&endDate={d.AddDays(7):yyyy-MM-ddTHH:mm:ssZ}"));
    foreach (var p in doc.RootElement.GetProperty("data").EnumerateArray())
        postTimes.Add(p.GetProperty("createdAt").GetDateTimeOffset());
}
postTimes = postTimes.Distinct().Order().ToList();
var firstPost = postTimes[0];
int CountBetween(DateTimeOffset a, DateTimeOffset b) => postTimes.Count(t => t >= a && t < b);

// Closed weekly events.
var windowRe = new Regex(@"from (?<m1>[A-Z][a-z]+) (?<d1>\d+),? (?:(?<y1>\d{4}),? )?(?<t1>\d+:\d+ [AP]M) ET to (?<m2>[A-Z][a-z]+) (?<d2>\d+), (?<y2>\d{4}),? (?<t2>\d+:\d+ [AP]M) ET");
var events = new List<Ev>();
for (var off = 0; ; off += 100)
{
    using var doc = JsonDocument.Parse(await Get($"https://gamma-api.polymarket.com/events?series_id=10000&closed=true&limit=100&offset={off}&order=endDate&ascending=false"));
    var arr = doc.RootElement.EnumerateArray().ToList();
    foreach (var e in arr)
    {
        var m = windowRe.Match(e.GetProperty("description").GetString() ?? "");
        if (!m.Success) continue;
        var y1 = m.Groups["y1"].Success ? m.Groups["y1"].Value : m.Groups["y2"].Value;
        DateTimeOffset P(string mo, string d, string y, string t) =>
            new(TimeZoneInfo.ConvertTimeToUtc(DateTime.ParseExact($"{mo} {d} {y} {t}", "MMMM d yyyy h:mm tt", CultureInfo.InvariantCulture), et), TimeSpan.Zero);
        var start = P(m.Groups["m1"].Value, m.Groups["d1"].Value, y1, m.Groups["t1"].Value);
        var end = P(m.Groups["m2"].Value, m.Groups["d2"].Value, m.Groups["y2"].Value, m.Groups["t2"].Value);
        if (start < firstPost.AddDays(1) || end > DateTimeOffset.UtcNow.AddDays(-1)) continue;

        var buckets = new List<Bk>();
        foreach (var mk in e.GetProperty("markets").EnumerateArray())
        {
            var label = mk.TryGetProperty("groupItemTitle", out var gt) ? gt.GetString() ?? "" : "";
            var r = Regex.Match(label, @"^(?:<(?<lt>\d+)|(?<a>\d+)\+|(?<a>\d+)-(?<b>\d+))$");
            var tokens = JsonSerializer.Deserialize<List<string>>(mk.GetProperty("clobTokenIds").GetString() ?? "[]")!;
            var prices = JsonSerializer.Deserialize<List<string>>(mk.GetProperty("outcomePrices").GetString() ?? "[]")!;
            if (!r.Success || tokens.Count == 0) continue;
            int? lo = r.Groups["lt"].Success ? null : int.Parse(r.Groups["a"].Value);
            int? hi = r.Groups["lt"].Success ? int.Parse(r.Groups["lt"].Value) - 1 : r.Groups["b"].Success ? int.Parse(r.Groups["b"].Value) : null;
            buckets.Add(new Bk(tokens[0], label, lo, hi, prices.FirstOrDefault() == "1"));
        }
        if (buckets.Count(b => b.Won) == 1)
            events.Add(new Ev(e.GetProperty("slug").GetString()!, start, end, buckets.OrderBy(b => b.Lo ?? -1).ToList()));
    }
    if (arr.Count < 100) break;
}
Console.WriteLine($"posts {postTimes.Count}; closed weekly events with a winner and post history: {events.Count}");

// 1) Count check.
int ok = 0, off1 = 0, far = 0;
foreach (var e in events)
{
    var n = CountBetween(e.Start, e.End);
    var w = e.Buckets.Single(b => b.Won);
    if (w.Contains(n)) ok++;
    else if (w.Contains(n - 3) || w.Contains(n + 3)) off1++;
    else far++;
}
Console.WriteLine($"[1] our count in winning bucket: {ok}/{events.Count}; within 3 posts: {off1}; further: {far}");

// Price history per bucket, hourly, last 4 days of each event.
async Task<List<(long T, double P)>> Hist(string token, DateTimeOffset from, DateTimeOffset to)
{
    using var doc = JsonDocument.Parse(await Get($"https://clob.polymarket.com/prices-history?market={token}&startTs={from.ToUnixTimeSeconds()}&endTs={to.ToUnixTimeSeconds()}&fidelity=60"));
    return doc.RootElement.GetProperty("history").EnumerateArray().Select(p => (p.GetProperty("t").GetInt64(), p.GetProperty("p").GetDouble())).ToList();
}
// No history at all means nobody traded the bucket: a dead tail priced near zero.
double? PriceAt(List<(long T, double P)> h, DateTimeOffset at)
{
    if (h.Count == 0) return 0.0005;
    var ts = at.ToUnixTimeSeconds();
    var last = h.LastOrDefault(p => p.T <= ts);
    return last == default ? null : last.P;
}

int[] horizons = [48, 24, 12, 6, 3];
var obs = new List<(int H, string Slug, Bk B, double Price, double SumPrices, int CountNow, DateTimeOffset At, Ev E)>();
foreach (var e in events)
{
    var hist = new Dictionary<string, List<(long, double)>>();
    foreach (var b in e.Buckets) hist[b.Token] = await Hist(b.Token, e.End.AddDays(-4), e.End);
    foreach (var h in horizons)
    {
        var at = e.End.AddHours(-h);
        var prices = e.Buckets.Select(b => (b, p: PriceAt(hist[b.Token], at))).ToList();
        if (prices.Any(x => x.p is null)) continue;
        var sum = prices.Sum(x => x.p!.Value);
        var countNow = CountBetween(e.Start, at);
        foreach (var (b, p) in prices) obs.Add((h, e.Slug, b, p!.Value, sum, countNow, at, e));
    }
}
Console.WriteLine($"price observations: {obs.Count}");

// 2) M-D: favourite at 90-99c.
static double Fee(double p) => 0.05 * p * (1 - p);
const double HalfSpread = 0.005;
Console.WriteLine();
Console.WriteLine("[2] favourite bought at 90-99c: horizon | trades | avg price | win rate | P&L per $1 staked after fee and half-spread");
foreach (var h in horizons)
{
    var fav = obs.Where(o => o.H == h).GroupBy(o => o.Slug).Select(g => g.MaxBy(o => o.Price)).Where(o => o.Price is >= 0.90 and < 0.995).ToList();
    if (fav.Count == 0) { Console.WriteLine($"  {h,2}h | 0"); continue; }
    var pnl = fav.Sum(o => (o.B.Won ? 1.0 : 0.0) - (o.Price + HalfSpread) - Fee(o.Price + HalfSpread));
    var staked = fav.Sum(o => o.Price + HalfSpread);
    Console.WriteLine($"  {h,2}h | {fav.Count,3} | {fav.Average(o => o.Price):F3} | {fav.Average(o => o.B.Won ? 1.0 : 0):P1} | {pnl / staked:+0.0%;-0.0%}");
}

// Market calibration across all buckets.
Console.WriteLine();
Console.WriteLine("    market calibration (all buckets, all horizons): price bin | n | avg price | actual win rate");
foreach (var g in obs.GroupBy(o => o.Price switch { < 0.02 => "<2c", < 0.10 => "2-10c", < 0.30 => "10-30c", < 0.60 => "30-60c", < 0.90 => "60-90c", _ => "90c+" })
             .OrderBy(g => g.Average(o => o.Price)))
    Console.WriteLine($"    {g.Key,6} | {g.Count(),5} | {g.Average(o => o.Price):F3} | {g.Average(o => o.B.Won ? 1.0 : 0):F3}");

// 3) M-B: pace model, walk-forward. Remaining posts ~ rate(last 7 days) * hours * empirical ratio,
// where the ratios come only from windows that ended before the prediction time.
Console.WriteLine();
Console.WriteLine("[3] log-loss of the winning bucket (lower is better): horizon | events | market | pace model | 50/50 blend");
foreach (var h in horizons)
{
    double llM = 0, llP = 0, llB = 0; int n = 0;
    foreach (var grp in obs.Where(o => o.H == h).GroupBy(o => o.Slug))
    {
        var at = grp.First().At;
        var e = grp.First().E;
        var countNow = grp.First().CountNow;
        var rate = CountBetween(at.AddDays(-7), at) / 168.0;

        // Historical ratios actual/expected for h-hour windows starting every 6 hours, ending before 'at'.
        var ratios = new List<double>();
        for (var s = firstPost.AddDays(7); s.AddHours(h) <= at; s = s.AddHours(6))
        {
            var r7 = CountBetween(s.AddDays(-7), s) / 168.0;
            if (r7 > 0) ratios.Add(CountBetween(s, s.AddHours(h)) / (r7 * h));
        }
        if (ratios.Count < 50) continue;

        var probs = grp.Select(o => o.B).ToDictionary(b => b, _ => 0.0);
        foreach (var r in ratios)
        {
            var final = countNow + (int)Math.Round(rate * h * r);
            var b = probs.Keys.FirstOrDefault(x => x.Contains(final));
            if (b is not null) probs[b] += 1.0 / ratios.Count;
        }

        var winner = grp.Single(o => o.B.Won);
        var pMarket = Math.Max(winner.Price / winner.SumPrices, 0.001);
        var pModel = Math.Max(probs[winner.B], 0.001);
        llM += -Math.Log(pMarket);
        llP += -Math.Log(pModel);
        llB += -Math.Log(Math.Max(0.5 * pMarket + 0.5 * pModel, 0.001));
        n++;
    }
    if (n > 0) Console.WriteLine($"  {h,2}h | {n,3} | {llM / n:F3} | {llP / n:F3} | {llB / n:F3}");
}

record Bk(string Token, string Label, int? Lo, int? Hi, bool Won)
{
    public bool Contains(int n) => (Lo is null || n >= Lo) && (Hi is null || n <= Hi);
}
record Ev(string Slug, DateTimeOffset Start, DateTimeOffset End, List<Bk> Buckets);
