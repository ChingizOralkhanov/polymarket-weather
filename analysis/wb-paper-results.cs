#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

// W-B paper results: every recorded dead bucket against how its market actually resolved.
var http = new HttpClient();
using var c = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
var signals = c.Query<Sig>(@"
    select w.market_id MarketId, e.slug Slug, w.city City, w.date Date, w.label Label, w.observed_max ObservedMax,
           w.metar_ts MetarTs, w.detected_at DetectedAt, w.no_best_ask NoAsk, w.no_ask_size NoSize, w.warning Warning
    from wb_signals w join events e on e.event_id = w.event_id").ToList();

var outcome = new Dictionary<string, string>();
foreach (var slug in signals.Select(s => s.Slug).Distinct())
{
    using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://gamma-api.polymarket.com/events/slug/{slug}"));
    foreach (var m in doc.RootElement.GetProperty("markets").EnumerateArray())
    {
        var prices = JsonSerializer.Deserialize<List<string>>(m.GetProperty("outcomePrices").GetString() ?? "[]")!;
        var resolved = m.TryGetProperty("umaResolutionStatus", out var st) && st.GetString() == "resolved";
        outcome[m.GetProperty("id").GetString()!] = !resolved ? "open" : prices[0] == "1" ? "Yes" : "No";
    }
}

static double Fee(double p) => 0.05 * p * (1 - p);
double Lag(Sig s) => (DateTimeOffset.Parse(s.DetectedAt, CultureInfo.InvariantCulture) - DateTimeOffset.FromUnixTimeSeconds(s.MetarTs)).TotalMinutes;

Console.WriteLine($"signals: {signals.Count}; resolved: {signals.Count(s => outcome.GetValueOrDefault(s.MarketId) is "Yes" or "No")}; still open: {signals.Count(s => outcome.GetValueOrDefault(s.MarketId) == "open")}");
Console.WriteLine($"'dead' buckets that actually won: {signals.Count(s => outcome.GetValueOrDefault(s.MarketId) == "Yes")}");
foreach (var s in signals.Where(s => outcome.GetValueOrDefault(s.MarketId) == "Yes"))
    Console.WriteLine($"  WON although dead: {s.City} {s.Date} [{s.Label}] METAR max {s.ObservedMax}; warning: {s.Warning ?? "-"}");
Console.WriteLine($"no sellers of No at all: {signals.Count(s => s.NoAsk is null)}; No ask >= 99c: {signals.Count(s => s.NoAsk >= 0.99)}; No ask < 99c: {signals.Count(s => s.NoAsk < 0.99)}");
Console.WriteLine($"fresh signals (detected within 20 min of the METAR): {signals.Count(s => Lag(s) <= 20)}, median lag {signals.Where(s => Lag(s) <= 20).Select(Lag).Order().ElementAtOrDefault(signals.Count(s => Lag(s) <= 20) / 2):F1} min");

Console.WriteLine();
Console.WriteLine("would-be trades (No ask < 99c):");
double pnl = 0, staked = 0;
foreach (var s in signals.Where(s => s.NoAsk < 0.99).OrderBy(s => s.DetectedAt))
{
    var o = outcome.GetValueOrDefault(s.MarketId, "?");
    var ask = s.NoAsk!.Value;
    var shares = Math.Min(s.NoSize ?? 0, 10);
    var result = o == "No" ? (1 - ask - Fee(ask)) * shares : o == "Yes" ? -(ask + Fee(ask)) * shares : 0;
    if (o is "Yes" or "No") { pnl += result; staked += ask * shares; }
    Console.WriteLine($"  {s.City,-13} {s.Date} [{s.Label,-14}] lag {Lag(s),6:F1} min  No {ask:F3} x {s.NoSize,7:F0}  outcome {o,-4} {(s.Warning is null ? "" : "WARN")}  10-share P&L {result:+0.00;-0.00}");
}
Console.WriteLine($"traded (resolved), up to 10 shares each: P&L ${pnl:F2} on ${staked:F2} staked");

record Sig(string MarketId, string Slug, string City, string Date, string Label, long ObservedMax, long MetarTs, string DetectedAt, double? NoAsk, double? NoSize, string? Warning);
