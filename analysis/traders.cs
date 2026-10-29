#:property PublishAot=false
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Weather traders from the Medium article (plus "meropi" from the Wenth paper): real P&L and how they trade.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
string[] names = ["WeatherHk", "HondaCivic", "ShyGuy1", "OnlyLuckNoBrain", "0X3573", "meropi"];
var dateRe = new Regex(@"on (?<m>[A-Z][a-z]+) (?<d>\d{1,2})(?:, (?<y>\d{4}))?\?");

foreach (var name in names)
{
    using var search = JsonDocument.Parse(await http.GetStringAsync($"https://gamma-api.polymarket.com/public-search?q={Uri.EscapeDataString(name)}&search_profiles=true&limit_per_type=10"));
    var profile = search.RootElement.TryGetProperty("profiles", out var ps)
        ? ps.EnumerateArray().FirstOrDefault(p => string.Equals(p.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
        : default;
    if (profile.ValueKind == JsonValueKind.Undefined) { Console.WriteLine($"== {name}: profile not found\n"); continue; }
    var wallet = profile.GetProperty("proxyWallet").GetString()!;

    async Task<string> Lb(string field, string period)
    {
        using var d = JsonDocument.Parse(await http.GetStringAsync($"https://data-api.polymarket.com/v1/leaderboard?user={wallet}&timePeriod={period}"));
        var arr = d.RootElement.EnumerateArray().ToList();
        return arr.Count == 0 ? "-" : arr[0].GetProperty(field).GetDouble().ToString("N0", CultureInfo.InvariantCulture);
    }
    Console.WriteLine($"== {name} {wallet[..6]}…{wallet[^4..]}: profit all {await Lb("pnl", "all")}, month {await Lb("pnl", "month")}, week {await Lb("pnl", "week")}; volume all {await Lb("vol", "all")}");

    // Last 1000 trades.
    var trades = new List<JsonElement>();
    for (var off = 0; off < 1000; off += 500)
    {
        using var d = JsonDocument.Parse(await http.GetStringAsync($"https://data-api.polymarket.com/activity?user={wallet}&limit=500&offset={off}&type=TRADE"));
        var page = d.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        trades.AddRange(page);
        if (page.Count < 500) break;
    }
    if (trades.Count == 0) { Console.WriteLine("   no trades\n"); continue; }

    var weather = trades.Where(t => (t.GetProperty("title").GetString() ?? "").Contains("temperature", StringComparison.OrdinalIgnoreCase)).ToList();
    var buys = weather.Where(t => t.GetProperty("side").GetString() == "BUY").ToList();
    var first = DateTimeOffset.FromUnixTimeSeconds(trades.Min(t => t.GetProperty("timestamp").GetInt64()));
    var last = DateTimeOffset.FromUnixTimeSeconds(trades.Max(t => t.GetProperty("timestamp").GetInt64()));
    Console.WriteLine($"   last {trades.Count} trades {first:MM-dd}..{last:MM-dd}: weather {weather.Count} ({weather.Count * 100 / trades.Count}%), buys {buys.Count}, sells {weather.Count - buys.Count}");
    if (buys.Count == 0) { Console.WriteLine(); continue; }

    var sizes = buys.Select(t => t.GetProperty("usdcSize").GetDouble()).Order().ToList();
    Console.WriteLine($"   buy size: median ${sizes[sizes.Count / 2]:F1}, p90 ${sizes[(int)(sizes.Count * 0.9)]:F0}, total ${sizes.Sum():N0}");

    Console.Write("   buy price: ");
    foreach (var (label, lo, hi) in new[] { ("<1c", 0.0, 0.01), ("1-10c", 0.01, 0.10), ("10-50c", 0.10, 0.50), ("50-90c", 0.50, 0.90), ("90-99c", 0.90, 0.99), (">=99c", 0.99, 1.01) })
        Console.Write($"{label} {buys.Count(t => t.GetProperty("price").GetDouble() is var p && p >= lo && p < hi) * 100 / buys.Count}%  ");
    Console.WriteLine();

    // When relative to the market's day: hours from 00:00 UTC of the date in the title.
    var hours = buys.Select(t =>
    {
        var m = dateRe.Match(t.GetProperty("title").GetString() ?? "");
        if (!m.Success) return (double?)null;
        var ts = DateTimeOffset.FromUnixTimeSeconds(t.GetProperty("timestamp").GetInt64());
        var y = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : ts.Year;
        if (!DateTime.TryParseExact($"{m.Groups["m"].Value} {m.Groups["d"].Value} {y}", "MMMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)) return null;
        return (ts - new DateTimeOffset(day, TimeSpan.Zero)).TotalHours;
    }).OfType<double>().ToList();
    if (hours.Count > 0)
        Console.WriteLine($"   buy timing vs the market day (UTC): before the day {hours.Count(h => h < 0) * 100 / hours.Count}%, during it {hours.Count(h => h is >= 0 and < 24) * 100 / hours.Count}%, after it {hours.Count(h => h >= 24) * 100 / hours.Count}%");

    // Typical "on the day, expensive" buys: price >= 90c bought during the market day.
    var late = buys.Where(t => t.GetProperty("price").GetDouble() >= 0.9).Take(3);
    foreach (var t in late)
        Console.WriteLine($"   e.g. {DateTimeOffset.FromUnixTimeSeconds(t.GetProperty("timestamp").GetInt64()):MM-dd HH:mm}Z {t.GetProperty("outcome").GetString()} @ {t.GetProperty("price").GetDouble():F3} ${t.GetProperty("usdcSize").GetDouble():F0}  {t.GetProperty("title").GetString()}");
    Console.WriteLine();
}
