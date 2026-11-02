#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using Dapper;
using Microsoft.Data.Sqlite;

// Is any price range mispriced on its own? Buys Yes or No of every resolved daily-max bucket at a fixed time before
// the end date, with a half-spread and the taker fee. Split by period: a real effect has to hold in both.
using var c = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
int[] horizons = [48, 36, 24, 12, 6];
const double HalfSpread = 0.01;
const string SplitMonth = "2026-05";
static double Fee(double p) => 0.05 * p * (1 - p);
(string Name, double Lo, double Hi)[] bins = [("5-10c", 0.05, 0.10), ("10-30c", 0.10, 0.30), ("30-50c", 0.30, 0.50), ("50-70c", 0.50, 0.70), ("70-75c", 0.70, 0.75), ("75-95c", 0.75, 0.95)];

foreach (var h in horizons)
{
    var rows = c.Query<(double? price, int won, string date)>($@"
        select (select p.price from prices p
                where p.market_id = b.market_id and p.ts <= cast(strftime('%s', e.end_date) as integer) - {h} * 3600
                order by p.ts desc limit 1) as price,
               case when b.outcome = 'Yes' then 1 else 0 end as won,
               e.date
        from buckets b join events e on e.event_id = b.event_id
        where e.kind = 'Max' and e.closed = 1 and b.outcome <> 'Unresolved' and e.end_date is not null")
        .Where(r => r.price is > 0).Select(r => (Price: r.price!.Value, Won: r.won == 1, Date: r.date)).ToList();

    Console.WriteLine($"=== {h} h before the end date (noon UTC): {rows.Count} buckets; ROI after {HalfSpread * 100:F0}c half-spread and fee");
    Console.WriteLine($"price Yes | buckets | avg | win rate | buy Yes: all, <{SplitMonth}, >={SplitMonth} | buy No: all, <{SplitMonth}, >={SplitMonth}");

    string Roi(IEnumerable<(double Price, bool Won, string Date)> xs, bool yes)
    {
        var l = xs.ToList();
        if (l.Count == 0) return "   -  ";
        var cost = l.Sum(r => { var p = (yes ? r.Price : 1 - r.Price) + HalfSpread; return p + Fee(p); });
        return $"{(l.Count(r => r.Won == yes) - cost) / cost,6:+0.0%;-0.0%}";
    }

    foreach (var (name, lo, hi) in bins)
    {
        var g = rows.Where(r => r.Price >= lo && r.Price < hi).ToList();
        if (g.Count == 0) continue;
        var early = g.Where(r => string.CompareOrdinal(r.Date, SplitMonth) < 0).ToList();
        var late = g.Where(r => string.CompareOrdinal(r.Date, SplitMonth) >= 0).ToList();
        Console.WriteLine($"{name,-9} | {g.Count,6} | {g.Average(r => r.Price) * 100,5:F1}c | {g.Average(r => r.Won ? 1.0 : 0) * 100,5:F1}% | {Roi(g, true)} {Roi(early, true)} {Roi(late, true)} | {Roi(g, false)} {Roi(early, false)} {Roi(late, false)}");
    }
    Console.WriteLine();
}
