#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
#:package Dapper@2.1.66
using Dapper;
using Microsoft.Data.Sqlite;

// Weather tails: do buckets priced at a fraction of a cent win more often than their price says?
using var c = new SqliteConnection(@"Data Source=C:\Users\shora\OneDrive\Desktop\polymarket-weather\data\weather.db;Mode=ReadOnly");
int[] horizons = [48, 24, 12];
static double Fee(double p) => 0.05 * p * (1 - p);

foreach (var h in horizons)
{
    // Price of every resolved daily-max bucket h hours before the event's end date (noon UTC of the day).
    var rows = c.Query<(double price, int won, string date)>($@"
        select (select p.price from prices p
                where p.market_id = b.market_id and p.ts <= cast(strftime('%s', e.end_date) as integer) - {h} * 3600
                order by p.ts desc limit 1) as price,
               case when b.outcome = 'Yes' then 1 else 0 end as won,
               e.date
        from buckets b join events e on e.event_id = b.event_id
        where e.kind = 'Max' and e.closed = 1 and b.outcome <> 'Unresolved' and e.end_date is not null").Where(r => r.price > 0).ToList();

    Console.WriteLine($"=== {h} h before the end date: {rows.Count} buckets");
    Console.WriteLine("price bin | buckets | avg price | win rate | wins | ROI buying Yes (after fee)");
    (string, double, double)[] bins = [("<=0.2c", 0, 0.0021), ("0.2-0.5c", 0.0021, 0.0051), ("0.5-1c", 0.0051, 0.0101), ("1-2c", 0.0101, 0.0201), ("2-5c", 0.0201, 0.0501), ("5-10c", 0.0501, 0.1001), ("10-30c", 0.1001, 0.3001)];
    foreach (var (name, lo, hi) in bins)
    {
        var g = rows.Where(r => r.price > lo && r.price <= hi).ToList();
        if (g.Count == 0) continue;
        var cost = g.Sum(r => r.price + Fee(r.price));
        var roi = (g.Sum(r => (double)r.won) - cost) / cost;
        Console.WriteLine($"{name,-9} | {g.Count,7} | {g.Average(r => r.price) * 100,6:F2}c | {g.Average(r => (double)r.won) * 100,6:F2}% | {g.Sum(r => r.won),5} | {roi,7:+0%;-0%}");
    }

    // Stability: the same cheap tail split by period.
    var cheap = rows.Where(r => r.price <= 0.0101).ToList();
    foreach (var period in cheap.GroupBy(r => r.date.Substring(0, 7)).OrderBy(g => g.Key))
    {
        var cost = period.Sum(r => r.price + Fee(r.price));
        Console.WriteLine($"   <=1c in {period.Key}: {period.Count(),6} buckets, {period.Sum(r => r.won),3} wins, ROI {(period.Sum(r => (double)r.won) - cost) / cost,6:+0%;-0%}");
    }
    Console.WriteLine();
}
