#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

// "The heat is over": given the running max and the current temperature at a local hour, how much can the max
// still rise? The rise distribution is learned only from METARs of 2016-2024 and tested against market prices of
// 2025-2026, so the model never sees the period it is judged on.
//
// Guards against a flattering test:
//  - the price is taken AFTER the hour (next hourly point, on average ~30 min later), so it already had time to
//    absorb every METAR the model used;
//  - the unit follows each event (early London markets were in Fahrenheit);
//  - one entry per bucket and side: the first hour the rule fires.
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var cache = Path.Combine(Path.GetTempPath(), "heat-cache");
Directory.CreateDirectory(cache);
using var db = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");

(string City, string Station, string Tz)[] cities =
[
    ("nyc", "KLGA", "America/New_York"),
    ("london", "EGLC", "Europe/London"),
    ("seoul", "RKSI", "Asia/Seoul"),
];
int[] hours = [10, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22];
const double HalfSpread = 0.01;
const double MinEdge = 0.05;
static double Fee(double p) => 0.05 * p * (1 - p);
static int Round(double v) => (int)Math.Floor(v + 0.5);
static int GapBin(int gap) => gap switch { <= 0 => 0, 1 => 1, 2 => 2, 3 or 4 => 3, _ => 4 };
static int Season(int month) => month is >= 4 and <= 9 ? 0 : 1;

var trades = new List<Trade>();

foreach (var (city, station, tzId) in cities)
{
    var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);

    var file = Path.Combine(cache, $"{station}.csv");
    if (!File.Exists(file))
    {
        var url = $"https://mesonet.agron.iastate.edu/cgi-bin/request/asos.py?station={station}&data=tmpc&data=tmpf&tz=UTC&format=onlycomma&missing=empty"
                  + "&year1=2016&month1=1&day1=1&year2=2025&month2=1&day2=1&report_type=3&report_type=4";
        await File.WriteAllTextAsync(file, await http.GetStringAsync(url));
        await Task.Delay(2000);
    }

    // Rise of the daily max after each hour, by (unit, hour, gap to the running max, season).
    var rises = new Dictionary<(bool F, int Hour, int Gap, int Season), List<int>>();
    var rows = File.ReadLines(file).Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= 4).ToList();
    foreach (var fahrenheit in new[] { false, true })
    {
        var train = rows.Where(p => p[fahrenheit ? 3 : 2].Length > 0)
            .Select(p => (Local: TimeZoneInfo.ConvertTime(DateTimeOffset.ParseExact(p[1], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), tz),
                Value: double.Parse(p[fahrenheit ? 3 : 2], CultureInfo.InvariantCulture)));
        foreach (var day in train.GroupBy(o => DateOnly.FromDateTime(o.Local.DateTime)))
        {
            var obs = day.OrderBy(o => o.Local).ToList();
            if (obs.Count < 16) continue;
            var finalMax = obs.Max(o => Round(o.Value));
            foreach (var h in hours)
            {
                var sofar = obs.Where(o => o.Local.Hour < h).ToList();
                if (sofar.Count < 4) continue;
                var max = sofar.Max(o => Round(o.Value));
                var key = (fahrenheit, h, GapBin(max - Round(sofar[^1].Value)), Season(day.Key.Month));
                if (!rises.TryGetValue(key, out var list)) rises[key] = list = [];
                list.Add(finalMax - max);
            }
        }
    }

    var events = db.Query<(string event_id, string date, string winning_market_id, string unit)>(
        "select event_id, date, winning_market_id, unit from events where city = @city and kind = 'Max' and closed = 1 and winning_market_id is not null and station_id = @station and date >= '2025-01-25' and date <= '2026-10-09'",
        new { city, station }).ToList();
    var observations = db.Query<(long ts, double? temp_c, double? temp_f)>(
            "select ts, temp_c, temp_f from observations where station_id = @station order by ts", new { station })
        .Select(o => (Local: TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(o.ts), tz), o.temp_c, o.temp_f))
        .ToList();
    var obsByDay = observations.ToLookup(o => DateOnly.FromDateTime(o.Local.DateTime));

    var score = new Dictionary<int, (double Market, double Model, double Blend, int N)>();
    var entered = new HashSet<(string Market, bool Yes)>();
    foreach (var e in events)
    {
        var fahrenheit = e.unit == "Fahrenheit";
        var day = DateOnly.Parse(e.date);
        var buckets = db.Query<(string market_id, long? lower, long? upper)>(
            "select market_id, lower, upper from buckets where event_id = @id", new { id = e.event_id }).ToList();
        var obs = obsByDay[day].Where(o => (fahrenheit ? o.temp_f : o.temp_c) is not null)
            .Select(o => (o.Local, Value: (fahrenheit ? o.temp_f : o.temp_c)!.Value)).OrderBy(o => o.Local).ToList();
        if (buckets.Count < 3 || obs.Count < 12) continue;

        foreach (var h in hours)
        {
            var sofar = obs.Where(o => o.Local.Hour < h).ToList();
            if (sofar.Count < 4) continue;
            var max = sofar.Max(o => Round(o.Value));
            var key = (fahrenheit, h, GapBin(max - Round(sofar[^1].Value)), Season(day.Month));
            if (!rises.TryGetValue(key, out var dist) || dist.Count < 50) continue;

            var local = day.ToDateTime(new TimeOnly(h, 0));
            var at = new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUnixTimeSeconds();
            var prices = new List<(string Id, double Price, double Model)>();
            var missing = false;
            foreach (var b in buckets)
            {
                var price = db.QueryFirstOrDefault<double?>(
                    "select price from prices where market_id = @id and ts >= @at and ts < @to order by ts limit 1",
                    new { id = b.market_id, at, to = at + 3600 });
                if (price is null) { missing = true; break; }
                var p = dist.Count(r =>
                {
                    var final = max + r;
                    return (b.lower is null || final >= b.lower) && (b.upper is null || final <= b.upper);
                }) / (double)dist.Count;
                prices.Add((b.market_id, price.Value, p));
            }
            if (missing) continue;

            var sum = prices.Sum(r => r.Price);
            if (sum < 0.5) continue;
            var w = prices.FirstOrDefault(r => r.Id == e.winning_market_id);
            if (w.Id is null) continue;

            var pMarket = Math.Max(w.Price / sum, 0.002);
            var pModel = Math.Max(w.Model, 0.002);
            var s = score.GetValueOrDefault(h);
            score[h] = (s.Market - Math.Log(pMarket), s.Model - Math.Log(pModel), s.Blend - Math.Log(Math.Max(0.5 * pMarket + 0.5 * pModel, 0.002)), s.N + 1);

            foreach (var r in prices.Where(r => r.Price is >= 0.03 and <= 0.97))
            {
                var won = r.Id == e.winning_market_id;
                var yesCost = r.Price + HalfSpread;
                var noCost = 1 - r.Price + HalfSpread;
                if (r.Model - yesCost - Fee(yesCost) >= MinEdge && entered.Add((r.Id, true)))
                    trades.Add(new Trade(city, h, e.date[..7], (won ? 1 : 0) - yesCost - Fee(yesCost), yesCost, true, r.Price, r.Model, won));
                else if (1 - r.Model - noCost - Fee(noCost) >= MinEdge && entered.Add((r.Id, false)))
                    trades.Add(new Trade(city, h, e.date[..7], (won ? 0 : 1) - noCost - Fee(noCost), noCost, false, r.Price, r.Model, won));
            }
        }
    }

    Console.WriteLine($"=== {city} ({station}): test events {events.Count}");
    Console.WriteLine("local hour | events | log-loss market | model | 50/50 blend   (lower is better)");
    foreach (var (h, s) in score.OrderBy(kv => kv.Key))
        Console.WriteLine($"  {h:00}:00 | {s.N,4} | {s.Market / s.N:F3} | {s.Model / s.N:F3} | {s.Blend / s.N:F3}");
    Console.WriteLine();
}

Console.WriteLine($"Fixed rule: enter once per bucket and side when model - price >= {MinEdge * 100:F0}c after a {HalfSpread * 100:F0}c half-spread and the fee");
void Report(string name, IEnumerable<Trade> xs)
{
    var l = xs.ToList();
    if (l.Count > 0)
        Console.WriteLine($"  {name,-20} trades {l.Count,5}  P&L per share ${l.Sum(t => t.Pnl),8:F2}  ROI {l.Sum(t => t.Pnl) / l.Sum(t => t.Stake),7:+0.0%;-0.0%}");
}
Report("all", trades);
foreach (var g in trades.GroupBy(t => t.City)) Report(g.Key, g);
Report("buy Yes", trades.Where(t => t.Yes));
Report("buy No", trades.Where(t => !t.Yes));
Report("No, model says dead", trades.Where(t => !t.Yes && t.Model == 0));
Report("No, model says alive", trades.Where(t => !t.Yes && t.Model > 0));
foreach (var g in trades.GroupBy(t => t.Hour).OrderBy(g => g.Key)) Report($"hour {g.Key:00}", g);
foreach (var g in trades.GroupBy(t => t.Month).OrderBy(g => g.Key)) Report(g.Key, g);

record Trade(string City, int Hour, string Month, double Pnl, double Stake, bool Yes, double Price, double Model, bool Won);
