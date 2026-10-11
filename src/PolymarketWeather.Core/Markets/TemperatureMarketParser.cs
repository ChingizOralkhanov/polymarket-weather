using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Core.Markets;

public static partial class TemperatureMarketParser
{
    [GeneratedRegex(@"^(?<kind>highest|lowest)-temperature-in-(?<city>.+)-on-(?<month>[a-z]+)-(?<day>\d{1,2})(?:-(?<year>\d{4}))?")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"timeseries\?site=(?<id>[A-Za-z0-9]{3,5})")]
    private static partial Regex NoaaSiteRegex();

    [GeneratedRegex(@"wunderground\.com/history/daily/[^\s""]*?/(?<id>[A-Z0-9]{4})\b")]
    private static partial Regex WundergroundStationRegex();

    // "61°F or below", "62-63°F", "13°C", "-2°C", "-3--2°C", "22°C or higher"
    [GeneratedRegex(@"^\s*(?<a>[-−]?\d+)(?:\s*-\s*(?<b>[-−]?\d+))?\s*°\s*[CF]\s*(?<tail>or below|or higher|or above)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BucketRegex();

    public static bool TryParse(GammaEvent ev, out TemperatureEvent? result, out string? error)
    {
        result = null;
        var slug = SlugRegex().Match(ev.Slug);
        if (!slug.Success)
        {
            error = $"unrecognised slug '{ev.Slug}'";
            return false;
        }

        if (!TryParseDate(slug, ev.EndDate, out var date))
        {
            error = $"cannot parse date from slug '{ev.Slug}'";
            return false;
        }

        var description = ev.Description ?? "";
        var unit = description.Contains("degrees Fahrenheit", StringComparison.OrdinalIgnoreCase)
            ? TemperatureUnit.Fahrenheit
            : TemperatureUnit.Celsius;
        var (sourceKind, stationId) = ParseSource(description, ev.ResolutionSource);

        var buckets = new List<TemperatureBucket>(ev.Markets.Count);
        foreach (var m in ev.Markets.OrderBy(m => int.TryParse(m.GroupItemThreshold, out var t) ? t : int.MaxValue))
        {
            if (!TryParseBucket(m, out var bucket, out error))
            {
                error = $"{ev.Slug}: {error}";
                return false;
            }
            buckets.Add(bucket!);
        }

        result = new TemperatureEvent(
            ev.Id,
            ev.Slug,
            slug.Groups["city"].Value,
            slug.Groups["kind"].Value == "highest" ? TemperatureKind.Max : TemperatureKind.Min,
            date,
            unit,
            sourceKind,
            stationId,
            ev.EndDate,
            buckets);
        error = null;
        return true;
    }

    public static bool TryParseRange(string label, out int? lower, out int? upper)
    {
        lower = upper = null;
        var m = BucketRegex().Match(label);
        if (!m.Success)
            return false;

        var a = ParseInt(m.Groups["a"].Value);
        var tail = m.Groups["tail"].Value.ToLowerInvariant();
        if (tail == "or below")
            upper = a;
        else if (tail is "or higher" or "or above")
            lower = a;
        else if (m.Groups["b"].Success)
            (lower, upper) = (a, ParseInt(m.Groups["b"].Value));
        else
            lower = upper = a;
        return true;
    }

    private static bool TryParseBucket(GammaMarket m, out TemperatureBucket? bucket, out string? error)
    {
        bucket = null;
        var label = m.GroupItemTitle ?? "";
        if (!TryParseRange(label, out var lower, out var upper))
        {
            error = $"cannot parse bucket '{label}'";
            return false;
        }

        var outcomes = DeserializeStringArray(m.OutcomesJson);
        var tokens = DeserializeStringArray(m.ClobTokenIdsJson);
        var yes = outcomes.FindIndex(o => o.Equals("Yes", StringComparison.OrdinalIgnoreCase));
        var no = outcomes.FindIndex(o => o.Equals("No", StringComparison.OrdinalIgnoreCase));
        if (yes < 0 || no < 0 || tokens.Count != outcomes.Count)
        {
            error = $"unexpected outcomes/tokens for market {m.Id}";
            return false;
        }

        bucket = new TemperatureBucket(m.Id, m.ConditionId, tokens[yes], tokens[no], label, lower, upper);
        error = null;
        return true;
    }

    private static (ResolutionSourceKind, string?) ParseSource(string description, string? resolutionSource)
    {
        var text = $"{resolutionSource} {description}";
        var noaa = NoaaSiteRegex().Match(text);
        if (noaa.Success)
            return (ResolutionSourceKind.NoaaTimeseries, noaa.Groups["id"].Value.ToUpperInvariant());

        var wu = WundergroundStationRegex().Match(text);
        if (wu.Success && description.Contains("Weather Underground", StringComparison.OrdinalIgnoreCase))
            return (ResolutionSourceKind.WeatherUnderground, wu.Groups["id"].Value);

        if (description.Contains("Hong Kong Observatory", StringComparison.OrdinalIgnoreCase))
            return (ResolutionSourceKind.HongKongObservatory, "HKO");

        return (ResolutionSourceKind.Unknown, null);
    }

    private static bool TryParseDate(Match slug, DateTimeOffset? endDate, out DateOnly date)
    {
        var year = slug.Groups["year"].Success
            ? int.Parse(slug.Groups["year"].Value, CultureInfo.InvariantCulture)
            : endDate?.Year ?? DateTime.UtcNow.Year;
        var text = $"{slug.Groups["month"].Value} {slug.Groups["day"].Value} {year}";
        return DateOnly.TryParseExact(text, "MMMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static int ParseInt(string s) =>
        int.Parse(s.Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static List<string> DeserializeStringArray(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
