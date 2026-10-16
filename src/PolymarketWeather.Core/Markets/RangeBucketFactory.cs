using System.Globalization;
using System.Text.Json;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Core.Markets;

/// <summary>Builds a bucket from a Gamma market once its range is known: tokens, resolution and close time.</summary>
internal static class RangeBucketFactory
{
    public static bool TryCreate(GammaMarket m, string label, int? lower, int? upper, out RangeBucket? bucket, out string? error)
    {
        bucket = null;
        var outcomes = DeserializeStringArray(m.OutcomesJson);
        var tokens = DeserializeStringArray(m.ClobTokenIdsJson);
        var yes = outcomes.FindIndex(o => o.Equals("Yes", StringComparison.OrdinalIgnoreCase));
        var no = outcomes.FindIndex(o => o.Equals("No", StringComparison.OrdinalIgnoreCase));
        if (yes < 0 || no < 0 || tokens.Count != outcomes.Count)
        {
            error = $"unexpected outcomes/tokens for market {m.Id}";
            return false;
        }

        bucket = new RangeBucket(
            m.Id,
            m.ConditionId,
            tokens[yes],
            tokens[no],
            label,
            lower,
            upper,
            ParseOutcome(m, yes),
            ParseClosedTime(m.ClosedTime));
        error = null;
        return true;
    }

    private static BucketOutcome ParseOutcome(GammaMarket m, int yesIndex)
    {
        if (!string.Equals(m.UmaResolutionStatus, "resolved", StringComparison.OrdinalIgnoreCase))
            return BucketOutcome.Unresolved;

        var prices = DeserializeStringArray(m.OutcomePricesJson);
        if (prices.Count <= yesIndex)
            return BucketOutcome.Unresolved;

        return prices[yesIndex] switch
        {
            "1" => BucketOutcome.Yes,
            "0" => BucketOutcome.No,
            _ => BucketOutcome.Unresolved,
        };
    }

    private static DateTimeOffset? ParseClosedTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static List<string> DeserializeStringArray(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
