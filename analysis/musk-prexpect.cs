#:property PublishAot=false
using System.Text.Json;

// How does Prexpect make money: realized P&L of closed positions by entry price, outcome side and time.
var http = new HttpClient();
const string Wallet = "0xa59c570a9eca148da55f6e1f47a538c0c600bb62";

var rows = new Dictionary<string, Pos>();
foreach (var dir in new[] { "DESC", "ASC" })
for (var off = 0; off < 1000; off += 50)
{
    var json = await http.GetStringAsync($"https://data-api.polymarket.com/closed-positions?user={Wallet}&limit=50&offset={off}&sortBy=REALIZEDPNL&sortDirection={dir}");
    var arr = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
    foreach (var p in arr)
        rows[p.GetProperty("asset").GetString()!] = new Pos(
            p.GetProperty("title").GetString()!,
            p.GetProperty("outcome").GetString()!,
            p.GetProperty("avgPrice").GetDouble(),
            p.GetProperty("totalBought").GetDouble(),
            p.GetProperty("realizedPnl").GetDouble(),
            p.GetProperty("curPrice").GetDouble(),
            DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("timestamp").GetInt64()));
    if (arr.Count < 50) break;
}

var all = rows.Values.ToList();
Console.WriteLine($"closed positions fetched: {all.Count} (top and bottom by P&L), sum P&L {all.Sum(p => p.Pnl):N0}");
Console.WriteLine($"winners {all.Count(p => p.Pnl > 0)}, losers {all.Count(p => p.Pnl < 0)}");
Console.WriteLine($"musk post-count markets: {all.Count(p => p.Title.Contains("Elon Musk post"))}");

Console.WriteLine();
Console.WriteLine("by entry price | positions | bought $ | P&L $ | resolved Yes-side share");
foreach (var g in all.GroupBy(p => p.AvgPrice switch { < 0.05 => "a) <5c", < 0.20 => "b) 5-20c", < 0.50 => "c) 20-50c", < 0.80 => "d) 50-80c", < 0.95 => "e) 80-95c", _ => "f) 95c+" }).OrderBy(g => g.Key))
    Console.WriteLine($"{g.Key,-10} | {g.Count(),5} | {g.Sum(p => p.Bought * p.AvgPrice),12:N0} | {g.Sum(p => p.Pnl),10:N0} | {g.Average(p => p.CurPrice):P0}");

Console.WriteLine();
Console.WriteLine("by side | positions | P&L $");
foreach (var g in all.GroupBy(p => p.Outcome))
    Console.WriteLine($"{g.Key,-4} | {g.Count(),5} | {g.Sum(p => p.Pnl),10:N0}");

Console.WriteLine();
Console.WriteLine("by month | positions | P&L $");
foreach (var g in all.GroupBy(p => p.Time.ToString("yyyy-MM")).OrderBy(g => g.Key))
    Console.WriteLine($"{g.Key} | {g.Count(),5} | {g.Sum(p => p.Pnl),10:N0}");

Console.WriteLine();
Console.WriteLine("top 10:");
foreach (var p in all.OrderByDescending(p => p.Pnl).Take(10))
    Console.WriteLine($"  {p.Pnl,9:N0}  {p.Outcome} @ {p.AvgPrice:F3}  bought {p.Bought:N0} sh  {p.Time:yyyy-MM-dd}  {p.Title}");
Console.WriteLine("bottom 10:");
foreach (var p in all.OrderBy(p => p.Pnl).Take(10))
    Console.WriteLine($"  {p.Pnl,9:N0}  {p.Outcome} @ {p.AvgPrice:F3}  bought {p.Bought:N0} sh  {p.Time:yyyy-MM-dd}  {p.Title}");

record Pos(string Title, string Outcome, double AvgPrice, double Bought, double Pnl, double CurPrice, DateTimeOffset Time);
