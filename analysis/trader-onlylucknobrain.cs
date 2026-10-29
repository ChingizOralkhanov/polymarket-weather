#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

// How does OnlyLuckNoBrain trade weather: timing vs model runs, cities, prices, and how the buys resolved.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
using var search = JsonDocument.Parse(await http.GetStringAsync("https://gamma-api.polymarket.com/public-search?q=OnlyLuckNoBrain&search_profiles=true&limit_per_type=5"));
var wallet = search.RootElement.GetProperty("profiles").EnumerateArray()
    .First(p => p.GetProperty("name").GetString() == "OnlyLuckNoBrain").GetProperty("proxyWallet").GetString()!;

var trades = new List<T>();
for (var off = 0; off < 10000; off += 500)
{
    string json;
    try { json = await http.GetStringAsync($"https://data-api.polymarket.com/activity?user={wallet}&limit=500&offset={off}&type=TRADE"); }
    catch (HttpRequestException) { break; }
    var page = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
    foreach (var e in page)
        trades.Add(new T(
            DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("timestamp").GetInt64()),
            e.GetProperty("side").GetString()!, e.GetProperty("outcome").GetString()!,
            e.GetProperty("price").GetDouble(), e.GetProperty("size").GetDouble(), e.GetProperty("usdcSize").GetDouble(),
            e.GetProperty("conditionId").GetString()!, e.GetProperty("title").GetString() ?? ""));
    if (page.Count < 500) break;
}
var buys = trades.Where(t => t.Side == "BUY" && t.Title.Contains("temperature", StringComparison.OrdinalIgnoreCase)).ToList();
Console.WriteLine($"trades fetched {trades.Count}, weather buys {buys.Count}, {trades.Min(t => t.Time):yyyy-MM-dd HH:mm}..{trades.Max(t => t.Time):yyyy-MM-dd HH:mm} UTC");
Console.WriteLine($"Yes {buys.Count(b => b.Outcome == "Yes") * 100 / buys.Count}% / No {buys.Count(b => b.Outcome == "No") * 100 / buys.Count}%, highest {buys.Count(b => b.Title.Contains("highest")) * 100 / buys.Count}% / lowest {buys.Count(b => b.Title.Contains("lowest")) * 100 / buys.Count}%");

Console.WriteLine();
Console.WriteLine("buys by UTC hour (count, $):   model data usually available: GFS ~03:30/09:30/15:30/21:30Z, ECMWF ~07:00/19:00Z, HRRR/NBM hourly");
foreach (var g in buys.GroupBy(b => b.Time.Hour).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key:00}h {g.Count(),5}  ${g.Sum(b => b.Usdc),7:N0}  {new string('#', Math.Min(60, g.Count() / 10))}");

// Bursts: minutes with many buys = one batch decision.
var bursts = buys.GroupBy(b => new DateTimeOffset(b.Time.Year, b.Time.Month, b.Time.Day, b.Time.Hour, b.Time.Minute, 0, TimeSpan.Zero))
    .Where(g => g.Count() >= 10).OrderByDescending(g => g.Count()).Take(12).ToList();
Console.WriteLine("\nbiggest one-minute bursts:");
foreach (var g in bursts.OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key:MM-dd HH:mm}Z  {g.Count(),3} buys  ${g.Sum(b => b.Usdc):N0}  cities: {string.Join(", ", g.Select(b => City(b.Title)).Distinct().Take(6))}");

Console.WriteLine("\ncities (buys, $):");
foreach (var g in buys.GroupBy(b => City(b.Title)).OrderByDescending(g => g.Sum(b => b.Usdc)).Take(15))
    Console.WriteLine($"  {g.Key,-16} {g.Count(),5}  ${g.Sum(b => b.Usdc),7:N0}  avg price {g.Average(b => b.Price):F2}");

// Resolution from our database (condition id -> bucket outcome).
using var db = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
var outcomes = db.Query<(string condition_id, string outcome)>("select condition_id, outcome from buckets where outcome <> 'Unresolved'")
    .ToDictionary(r => r.condition_id, r => r.outcome);
var resolved = buys.Where(b => outcomes.ContainsKey(b.ConditionId)).ToList();
static double Fee(double p) => 0.05 * p * (1 - p);
bool Won(T b) => (b.Outcome == "Yes") == (outcomes[b.ConditionId] == "Yes");
Console.WriteLine($"\nbuys whose market is resolved in our DB: {resolved.Count}");
if (resolved.Count > 0)
{
    double Roi(IEnumerable<T> xs) { var l = xs.ToList(); var cost = l.Sum(b => b.Size * (b.Price + Fee(b.Price))); return (l.Where(Won).Sum(b => b.Size) - cost) / cost; }
    Console.WriteLine($"  win rate {resolved.Count(Won) * 100.0 / resolved.Count:F1}%, ROI after fee {Roi(resolved):+0.0%;-0.0%}, P&L ${resolved.Where(Won).Sum(b => b.Size) - resolved.Sum(b => b.Size * (b.Price + Fee(b.Price))):N0}");
    Console.WriteLine("  by price paid | buys | win rate | avg price | ROI");
    foreach (var (label, lo, hi) in new[] { ("<10c", 0.0, 0.1), ("10-30c", 0.1, 0.3), ("30-50c", 0.3, 0.5), ("50-70c", 0.5, 0.7), ("70-90c", 0.7, 0.9), (">=90c", 0.9, 1.01) })
    {
        var g = resolved.Where(b => b.Price >= lo && b.Price < hi).ToList();
        if (g.Count > 0) Console.WriteLine($"  {label,-7} | {g.Count,5} | {g.Count(Won) * 100.0 / g.Count,6:F1}% | {g.Average(b => b.Price):F2} | {Roi(g):+0.0%;-0.0%}");
    }
    Console.WriteLine("  by hour bought (UTC) | buys | ROI");
    foreach (var g in resolved.GroupBy(b => b.Time.Hour / 3 * 3).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key:00}-{g.Key + 3:00}h | {g.Count(),5} | {Roi(g):+0.0%;-0.0%}");
}

static string City(string title)
{
    var m = Regex.Match(title, @"temperature in (?<c>.+?) (?:be|on) ");
    return m.Success ? m.Groups["c"].Value : "?";
}

record T(DateTimeOffset Time, string Side, string Outcome, double Price, double Size, double Usdc, string ConditionId, string Title);
