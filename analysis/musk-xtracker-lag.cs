#:property PublishAot=false
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Does the price of the weekly post-count market move when posts are published,
// or only when xtracker (the resolution counter) imports them late?
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

// 1. All posts from xtracker, week by week.
var posts = new List<(DateTimeOffset Created, DateTimeOffset Imported)>();
for (var d = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero); d < DateTimeOffset.UtcNow; d = d.AddDays(7))
{
    var url = $"https://xtracker.polymarket.com/api/users/elonmusk/posts?startDate={d:yyyy-MM-ddTHH:mm:ssZ}&endDate={d.AddDays(7):yyyy-MM-ddTHH:mm:ssZ}";
    using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
    foreach (var p in doc.RootElement.GetProperty("data").EnumerateArray())
        if (p.TryGetProperty("importedAt", out var imp) && imp.ValueKind == JsonValueKind.String)
            posts.Add((p.GetProperty("createdAt").GetDateTimeOffset(), imp.GetDateTimeOffset()));
}
posts = posts.DistinctBy(p => (p.Created, p.Imported)).OrderBy(p => p.Created).ToList();
Console.WriteLine($"posts: {posts.Count}, {posts.First().Created:yyyy-MM-dd}..{posts.Last().Created:yyyy-MM-dd}");
var lags = posts.Select(p => (p.Imported - p.Created).TotalMinutes).ToList();
Console.WriteLine($"lag: <5m {lags.Count(l => l < 5) * 100.0 / lags.Count:F0}%, 5-60m {lags.Count(l => l is >= 5 and < 60) * 100.0 / lags.Count:F0}%, 1-6h {lags.Count(l => l is >= 60 and < 360) * 100.0 / lags.Count:F0}%, >6h {lags.Count(l => l >= 360) * 100.0 / lags.Count:F0}%");

// 2. Late batches: posts imported at least an hour late, grouped by import time.
var batches = posts.Where(p => p.Imported - p.Created >= TimeSpan.FromHours(1))
    .GroupBy(p => new DateTimeOffset(p.Imported.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero))
    .Select(g => (Imported: g.Key, FirstCreated: g.Min(p => p.Created), Count: g.Count()))
    .Where(b => b.Count >= 3)
    .OrderBy(b => b.Imported)
    .ToList();
Console.WriteLine($"late batches (>=3 posts, >=1h late): {batches.Count}");
foreach (var b in batches)
    Console.WriteLine($"  imported {b.Imported:yyyy-MM-dd HH:mm}  posts {b.Count,4}  first created {b.FirstCreated:yyyy-MM-dd HH:mm}  lag {(b.Imported - b.FirstCreated).TotalHours:F1} h");
var lateSingles = posts.Count(p => p.Imported - p.Created >= TimeSpan.FromHours(1));
Console.WriteLine($"posts >=1h late in total: {lateSingles}");

// 3. Weekly events with their counting window.
var windowRe = new Regex(@"from (?<m1>[A-Z][a-z]+) (?<d1>\d+),? (?:(?<y1>\d{4}),? )?(?<t1>\d+:\d+ [AP]M) ET to (?<m2>[A-Z][a-z]+) (?<d2>\d+), (?<y2>\d{4}),? (?<t2>\d+:\d+ [AP]M) ET");
var events = new List<(string Slug, DateTimeOffset Start, DateTimeOffset End, List<(string Token, double Mid)> Buckets)>();
for (var off = 0; ; off += 100)
{
    using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://gamma-api.polymarket.com/events?series_id=10000&limit=100&offset={off}&order=endDate&ascending=false"));
    var arr = doc.RootElement.EnumerateArray().ToList();
    foreach (var e in arr)
    {
        var m = windowRe.Match(e.GetProperty("description").GetString() ?? "");
        if (!m.Success) continue;
        var y1 = m.Groups["y1"].Success ? m.Groups["y1"].Value : m.Groups["y2"].Value;
        DateTimeOffset Parse(string mo, string d, string y, string t) =>
            new(TimeZoneInfo.ConvertTimeToUtc(DateTime.ParseExact($"{mo} {d} {y} {t}", "MMMM d yyyy h:mm tt", CultureInfo.InvariantCulture), et), TimeSpan.Zero);
        var start = Parse(m.Groups["m1"].Value, m.Groups["d1"].Value, y1, m.Groups["t1"].Value);
        var end = Parse(m.Groups["m2"].Value, m.Groups["d2"].Value, m.Groups["y2"].Value, m.Groups["t2"].Value);
        if (end < posts.First().Created) continue;

        var buckets = new List<(string, double)>();
        foreach (var mk in e.GetProperty("markets").EnumerateArray())
        {
            var label = mk.TryGetProperty("groupItemTitle", out var gt) ? gt.GetString() ?? "" : "";
            var tokens = JsonSerializer.Deserialize<List<string>>(mk.GetProperty("clobTokenIds").GetString() ?? "[]")!;
            var r = Regex.Match(label, @"^(?:<(?<lt>\d+)|(?<a>\d+)\+|(?<a>\d+)-(?<b>\d+))$");
            if (!r.Success || tokens.Count == 0) continue;
            double mid = r.Groups["lt"].Success ? int.Parse(r.Groups["lt"].Value) - 10
                : r.Groups["b"].Success ? (int.Parse(r.Groups["a"].Value) + int.Parse(r.Groups["b"].Value)) / 2.0
                : int.Parse(r.Groups["a"].Value) + 10;
            buckets.Add((tokens[0], mid));
        }
        events.Add((e.GetProperty("slug").GetString()!, start, end, buckets));
    }
    if (arr.Count < 100 || events.Count > 0 && events.Min(x => x.End) < posts.First().Created) break;
}
Console.WriteLine($"weekly events covering the post history: {events.Count}");

// 4. Implied expected count from bucket prices around each late batch.
async Task<Dictionary<string, List<(long T, double P)>>> History(IEnumerable<string> tokens, DateTimeOffset from, DateTimeOffset to)
{
    var result = new Dictionary<string, List<(long, double)>>();
    foreach (var t in tokens)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(
            $"https://clob.polymarket.com/prices-history?market={t}&startTs={from.ToUnixTimeSeconds()}&endTs={to.ToUnixTimeSeconds()}&fidelity=5"));
        result[t] = doc.RootElement.GetProperty("history").EnumerateArray()
            .Select(p => (p.GetProperty("t").GetInt64(), p.GetProperty("p").GetDouble())).ToList();
    }
    return result;
}

double? Implied(Dictionary<string, List<(long T, double P)>> h, List<(string Token, double Mid)> buckets, DateTimeOffset at)
{
    var ts = at.ToUnixTimeSeconds();
    double sumP = 0, sumPM = 0;
    foreach (var (token, mid) in buckets)
    {
        var last = h[token].LastOrDefault(p => p.T <= ts);
        if (last == default) continue;
        sumP += last.P; sumPM += last.P * mid;
    }
    return sumP > 0.5 ? sumPM / sumP : null;
}

Console.WriteLine();
Console.WriteLine("batch imported (UTC) | posts | lag h | on-time posts in gap | mean before | at import-5m | +30m after | move during gap | jump at import");
var rows = new List<(int K, double Gap, double Jump)>();
foreach (var b in batches.Where(b => b.Imported - b.FirstCreated < TimeSpan.FromDays(5)))
foreach (var ev in events.Where(e => e.Buckets.Count > 0 && b.Imported > e.Start && b.Imported < e.End.AddHours(-2)))
{
    // Late posts of this batch that count towards this event's window.
    var batchPosts = posts.Where(p => p.Imported >= b.Imported && p.Imported < b.Imported.AddMinutes(1)
                                      && p.Imported - p.Created >= TimeSpan.FromHours(1)
                                      && p.Created >= ev.Start && p.Created < ev.End).ToList();
    if (batchPosts.Count < 3)
    {
        Console.WriteLine($"{b.Imported:yyyy-MM-dd HH:mm} | {ev.Slug}: only {batchPosts.Count} late posts in its window, skipped");
        continue;
    }
    var k = batchPosts.Count;
    var t0 = batchPosts.Min(p => p.Created).AddMinutes(-15);
    var t1 = b.Imported.AddMinutes(-5);
    var t2 = b.Imported.AddMinutes(30);
    var h = await History(ev.Buckets.Select(x => x.Token), t0.AddHours(-1), t2.AddHours(1));
    var (m0, m1, m2) = (Implied(h, ev.Buckets, t0), Implied(h, ev.Buckets, t1), Implied(h, ev.Buckets, t2));
    if (m0 is null || m1 is null || m2 is null)
    {
        Console.WriteLine($"{b.Imported:yyyy-MM-dd HH:mm} | {ev.Slug}: no price history, skipped");
        continue;
    }

    var onTime = posts.Count(p => p.Imported > t0 && p.Imported <= t1 && p.Imported - p.Created < TimeSpan.FromHours(1));
    var jump = m2.Value - m1.Value;
    rows.Add((k, m1.Value - m0.Value, jump));
    Console.WriteLine($"{b.Imported:yyyy-MM-dd HH:mm} | {ev.Slug.Replace("elon-musk-of-tweets-", "")} | {k,3} | {(b.Imported - t0).TotalHours,5:F1} | {onTime,3} | {m0,6:F1} | {m1,6:F1} | {m2,6:F1} | {m1 - m0,6:+0.0;-0.0} | {jump,6:+0.0;-0.0}");
}

if (rows.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"batches analysed: {rows.Count}, total late posts: {rows.Sum(r => r.K)}");
    Console.WriteLine($"avg jump at import: {rows.Average(r => r.Jump):+0.0;-0.0} posts, avg batch size {rows.Average(r => r.K):F1}");
    Console.WriteLine($"share of batch size priced only at import (avg jump / k): {rows.Average(r => r.Jump / r.K):P0}");
}
