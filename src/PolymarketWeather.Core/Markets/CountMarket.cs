using System.Globalization;
using System.Text.RegularExpressions;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Core.Markets;

/// <summary>A "how many posts" event, e.g. "Elon Musk # tweets October 13 - October 20, 2026?".</summary>
public sealed record CountEvent(
    string EventId,
    string Slug,
    string? SeriesId,
    string Title,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? EndDate,
    bool Closed,
    decimal? Volume,
    string? Description,
    IReadOnlyList<RangeBucket> Buckets)
{
    public RangeBucket? WinningBucket => Buckets.SingleOrDefault(b => b.Outcome == BucketOutcome.Yes);
}

public static partial class CountMarketParser
{
    // "<20", "20-39", "500+"
    [GeneratedRegex(@"^\s*(?:(?<lt><)\s*(?<a>\d+)|(?<a>\d+)\s*(?<plus>\+)|(?<a>\d+)\s*[-–—]\s*(?<b>\d+))\s*$")]
    private static partial Regex BucketRegex();

    public static bool TryParse(GammaEvent ev, out CountEvent? result, out string? error)
    {
        result = null;
        var buckets = new List<RangeBucket>(ev.Markets.Count);
        foreach (var m in ev.Markets.OrderBy(m => int.TryParse(m.GroupItemThreshold, out var t) ? t : int.MaxValue))
        {
            var label = m.GroupItemTitle ?? "";
            if (!TryParseRange(label, out var lower, out var upper))
            {
                error = $"{ev.Slug}: cannot parse bucket '{label}'";
                return false;
            }
            if (!RangeBucketFactory.TryCreate(m, label, lower, upper, out var bucket, out error))
            {
                error = $"{ev.Slug}: {error}";
                return false;
            }
            buckets.Add(bucket!);
        }

        result = new CountEvent(ev.Id, ev.Slug, ev.Series.FirstOrDefault()?.Id, ev.Title, ev.CreatedAt, ev.EndDate,
            ev.Closed, ev.Volume, ev.Description, buckets);
        error = null;
        return true;
    }

    public static bool TryParseRange(string label, out int? lower, out int? upper)
    {
        lower = upper = null;
        var m = BucketRegex().Match(label);
        if (!m.Success)
            return false;

        var a = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["lt"].Success)
            upper = a - 1;
        else if (m.Groups["plus"].Success)
            lower = a;
        else
            (lower, upper) = (a, int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture));
        return true;
    }
}
