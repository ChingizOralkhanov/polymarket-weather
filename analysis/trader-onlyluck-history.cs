#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

// Where did OnlyLuckNoBrain's profit come from: closed positions over the whole history, then all activity of the
// last days position by position (buys, sells, merges and redemptions), which shows whether exits matter.
const string Wallet = "0x6a8d1709bfb718d8555d315a983c4816278350f9";
var days = args.Length > 0 ? int.Parse(args[0]) : 14;
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

async Task<List<JsonElement>> Get(string url)
{
    for (var attempt = 0; ; attempt++)
    {
        try { return JsonDocument.Parse(await http.GetStringAsync(url)).RootElement.EnumerateArray().ToList(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < 4) { await Task.Delay(3000 * (attempt + 1)); }
    }
}

Console.WriteLine("leaderboard:");
foreach (var period in new[] { "all", "month", "week" })
{
    var r = (await Get($"https://data-api.polymarket.com/v1/leaderboard?user={Wallet}&timePeriod={period}"))[0];
    Console.WriteLine($"  {period,-6} P&L {r.GetProperty("pnl").GetDouble(),10:N0}  volume {r.GetProperty("vol").GetDouble(),12:N0}");
}

var closed = new Dictionary<string, Closed>();
for (var off = 0; off < 200000; off += 50)
{
    var page = await Get($"https://data-api.polymarket.com/closed-positions?user={Wallet}&limit=50&offset={off}&sortBy=TIMESTAMP&sortDirection=ASC");
    foreach (var p in page)
        closed[p.GetProperty("asset").GetString()!] = new Closed(p.GetProperty("title").GetString()!, p.GetProperty("outcome").GetString()!,
            p.GetProperty("avgPrice").GetDouble(), p.GetProperty("totalBought").GetDouble(), p.GetProperty("realizedPnl").GetDouble(),
            DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("timestamp").GetInt64()));
    if (page.Count < 50) break;
}
var all = closed.Values.ToList();
bool Weather(string title) => title.Contains("temperature", StringComparison.OrdinalIgnoreCase);

void Table(string title, IEnumerable<IGrouping<string, Closed>> groups)
{
    Console.WriteLine($"\n{title} | positions | spent $ | P&L $ | ROI");
    foreach (var g in groups.OrderBy(g => g.Key))
    {
        var spent = g.Sum(p => p.Bought * p.AvgPrice);
        Console.WriteLine($"  {g.Key,-14} | {g.Count(),6} | {spent,10:N0} | {g.Sum(p => p.Pnl),9:N0} | {g.Sum(p => p.Pnl) / Math.Max(spent, 1),7:+0.0%;-0.0%}");
    }
}

Console.WriteLine($"\nclosed positions {all.Count}, {all.Min(p => p.Time):yyyy-MM-dd}..{all.Max(p => p.Time):yyyy-MM-dd}, sum P&L {all.Sum(p => p.Pnl):N0}");
Table("market type", all.GroupBy(p => Weather(p.Title) ? "weather" : "other"));
Table("month", all.GroupBy(p => p.Time.ToString("yyyy-MM")));
var weather = all.Where(p => Weather(p.Title)).ToList();
Table("weather, side", weather.GroupBy(p => p.Outcome));
Table("weather, entry", weather.GroupBy(p => p.AvgPrice switch { < 0.1 => "a <10c", < 0.3 => "b 10-30c", < 0.5 => "c 30-50c", < 0.7 => "d 50-70c", < 0.9 => "e 70-90c", _ => "f 90c+" }));
Table("weather, side+entry", weather.GroupBy(p => p.Outcome + " " + (p.AvgPrice switch { < 0.3 => "a <30c", < 0.7 => "b 30-70c", _ => "c 70c+" })));

Console.WriteLine("\nbiggest closed positions by P&L:");
foreach (var p in all.OrderByDescending(p => Math.Abs(p.Pnl)).Take(12))
    Console.WriteLine($"  {p.Pnl,8:N0}  {p.Outcome} @ {p.AvgPrice:F3}  {p.Bought,8:N0} sh  {p.Time:yyyy-MM-dd}  {p.Title}");
var top = all.OrderByDescending(p => p.Pnl).ToList();
var positive = all.Where(p => p.Pnl > 0).Sum(p => p.Pnl);
Console.WriteLine($"share of gross profit in the top 1% of winners: {top.Take(Math.Max(1, all.Count / 100)).Sum(p => p.Pnl) / positive:P0}");

var open = new List<JsonElement>();
for (var off = 0; off < 20000; off += 500)
{
    var page = await Get($"https://data-api.polymarket.com/positions?user={Wallet}&limit=500&offset={off}");
    open.AddRange(page);
    if (page.Count < 500) break;
}
Console.WriteLine($"\nopen positions {open.Count}: value now {open.Sum(p => p.GetProperty("currentValue").GetDouble()):N0}, unrealized P&L {open.Sum(p => p.GetProperty("cashPnl").GetDouble()):N0}");

// Recent activity, walking backwards by time because offset paging stops after a few thousand rows.
var since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
var activity = new Dictionary<string, Act>();
var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
while (end > since)
{
    var page = await Get($"https://data-api.polymarket.com/activity?user={Wallet}&limit=500&end={end}");
    if (page.Count == 0) break;
    foreach (var e in page)
    {
        var a = new Act(e.GetProperty("timestamp").GetInt64(), e.GetProperty("type").GetString()!,
            e.TryGetProperty("side", out var s) ? s.GetString() ?? "" : "", e.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "",
            e.GetProperty("price").GetDouble(), e.GetProperty("size").GetDouble(), e.GetProperty("usdcSize").GetDouble(),
            e.GetProperty("conditionId").GetString() ?? "", e.GetProperty("title").GetString() ?? "");
        activity[$"{e.GetProperty("transactionHash").GetString()}|{a.ConditionId}|{a.Type}|{a.Side}|{a.Outcome}|{a.Size}|{a.Price}"] = a;
    }
    var oldest = page.Min(e => e.GetProperty("timestamp").GetInt64());
    end = oldest < end ? oldest : end - 1;
}
var recent = activity.Values.Where(a => a.Ts >= since).ToList();
Console.WriteLine($"\nactivity of the last {days} days: {recent.Count} rows");
Console.WriteLine("type/side | market | rows | $");
foreach (var g in recent.GroupBy(a => (a.Type, a.Side, Kind: Weather(a.Title) ? "weather" : "other")).OrderByDescending(g => g.Sum(a => a.Usdc)))
    Console.WriteLine($"  {g.Key.Type + " " + g.Key.Side,-12} | {g.Key.Kind,-7} | {g.Count(),6} | {g.Sum(a => a.Usdc),10:N0}");

using var db = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
var buckets = db.Query<(string condition_id, string outcome, string event_id, string end_date)>(
        "select b.condition_id, b.outcome, b.event_id, e.end_date from buckets b join events e on e.event_id = b.event_id where e.end_date is not null")
    .ToDictionary(r => r.condition_id);
var trades = recent.Where(a => a.Type == "TRADE" && Weather(a.Title) && buckets.ContainsKey(a.ConditionId)).ToList();

// How long before the end date he buys, and how the buys did if held.
Console.WriteLine("\nweather buys by hours before the end date (noon UTC) | buys | $ | ROI if held, resolved only");
foreach (var g in trades.Where(a => a.Side == "BUY").GroupBy(a =>
         {
             var h = (DateTimeOffset.Parse(buckets[a.ConditionId].end_date).ToUnixTimeSeconds() - a.Ts) / 3600.0;
             return h switch { > 48 => "a >48h", > 24 => "b 24-48h", > 12 => "c 12-24h", > 0 => "d 0-12h", _ => "e after noon" };
         }).OrderBy(g => g.Key))
{
    var done = g.Where(a => buckets[a.ConditionId].outcome != "Unresolved").ToList();
    var cost = done.Sum(a => a.Usdc);
    var pay = done.Where(a => (a.Outcome == "Yes") == (buckets[a.ConditionId].outcome == "Yes")).Sum(a => a.Size);
    Console.WriteLine($"  {g.Key,-13} | {g.Count(),6} | {g.Sum(a => a.Usdc),9:N0} | {(pay - cost) / Math.Max(cost, 1),7:+0.0%;-0.0%}");
}

// Position by position: what exits changed against holding everything to the end.
var positions = trades.GroupBy(a => (a.ConditionId, a.Outcome)).Where(g => buckets[g.Key.ConditionId].outcome != "Unresolved" && g.Any(a => a.Side == "BUY")).ToList();
double held = 0, actual = 0, spentAll = 0; var sold = 0;
foreach (var g in positions)
{
    var won = (g.Key.Outcome == "Yes") == (buckets[g.Key.ConditionId].outcome == "Yes");
    var bought = g.Where(a => a.Side == "BUY").Sum(a => a.Size);
    var soldSize = g.Where(a => a.Side == "SELL").Sum(a => a.Size);
    var cost = g.Where(a => a.Side == "BUY").Sum(a => a.Usdc);
    var income = g.Where(a => a.Side == "SELL").Sum(a => a.Usdc);
    if (soldSize > 0) sold++;
    held += (won ? bought : 0) - cost;
    actual += income + (won ? Math.Max(0, bought - soldSize) : 0) - cost;
    spentAll += cost;
}
Console.WriteLine($"\nresolved weather positions {positions.Count}, with sells {sold}, spent {spentAll:N0}: P&L if held {held:N0} ({held / Math.Max(spentAll, 1):+0.0%;-0.0%}), P&L with his sells {actual:N0} ({actual / Math.Max(spentAll, 1):+0.0%;-0.0%})");

// Does he hold several buckets of one event (a ladder) and both sides of one bucket?
var perEvent = trades.Where(a => a.Side == "BUY").GroupBy(a => buckets[a.ConditionId].event_id).Select(g => g.Select(a => a.ConditionId).Distinct().Count()).Order().ToList();
if (perEvent.Count > 0)
    Console.WriteLine($"events bought {perEvent.Count}: buckets per event median {perEvent[perEvent.Count / 2]}, p90 {perEvent[perEvent.Count * 9 / 10]}");
var bothSides = trades.Where(a => a.Side == "BUY").GroupBy(a => a.ConditionId).Count(g => g.Select(a => a.Outcome).Distinct().Count() == 2);
Console.WriteLine($"buckets bought on both sides: {bothSides} of {trades.Where(a => a.Side == "BUY").Select(a => a.ConditionId).Distinct().Count()}");

record Closed(string Title, string Outcome, double AvgPrice, double Bought, double Pnl, DateTimeOffset Time);
record Act(long Ts, string Type, string Side, string Outcome, double Price, double Size, double Usdc, string ConditionId, string Title);
