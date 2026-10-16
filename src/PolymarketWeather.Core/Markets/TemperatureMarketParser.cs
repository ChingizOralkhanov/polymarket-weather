using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Core.Markets;

public static partial class TemperatureMarketParser
{
    // Old slugs: "...-on-jan-22", "...-on-may-8-625", "arch-..."; new: "...-on-october-12-2026".
    [GeneratedRegex(@"^(?:arch-)*(?<kind>highest|lowest)-temperature-in-(?<city>.+?)-on-(?<month>[a-z]+)-(?<day>\d{1,2})(?:-(?<year>\d{4}))?(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"timeseries\?site=(?<id>[A-Za-z0-9]{3,5})")]
    private static partial Regex NoaaSiteRegex();

    // ".../history/daily/us/ny/new-york-city/KLGA" or ".../weather/KORD"
    [GeneratedRegex(@"wunderground\.com/[^\s""]*?/(?<id>[A-Z][A-Z0-9]{3})\b")]
    private static partial Regex WundergroundStationRegex();

    // "61°F or below", "16°F or lower", "62-63°F", "67–68°F", "13°C", "-2°C", "-3--2°C", "22°C or higher",
    // early ones: "<29°F", "57 or higher"
    [GeneratedRegex(@"^\s*(?<cmp>[<>])?\s*(?<a>[-−]?\d+)(?:\s*[-–—]\s*(?<b>[-−]?\d+))?(?:\s*°?\s*[CF])?\s*(?<tail>or below|or lower|or higher|or above)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BucketRegex();

    private static readonly string[] MonthPrefixes = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, string> CityAliases = new()
    {
        ["new-york-city"] = "nyc",
    };

    public static bool TryParse(GammaEvent ev, out TemperatureEvent? result, out string? error)
    {
        result = null;
        var slug = SlugRegex().Match(ev.Slug);
        if (!slug.Success)
        {
            error = $"unrecognised slug '{ev.Slug}'";
            return false;
        }

        if (!TryParseDate(slug, ev.CreatedAt ?? ev.EndDate, out var date))
        {
            error = $"cannot parse date from slug '{ev.Slug}'";
            return false;
        }

        var description = ev.Description ?? "";
        var unit = description.Contains("degrees Fahrenheit", StringComparison.OrdinalIgnoreCase)
            ? TemperatureUnit.Fahrenheit
            : TemperatureUnit.Celsius;
        var (sourceKind, stationId) = ParseSource(description, ev.ResolutionSource);

        var buckets = new List<RangeBucket>(ev.Markets.Count);
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
            ev.Series.FirstOrDefault()?.Id,
            CityAliases.GetValueOrDefault(slug.Groups["city"].Value, slug.Groups["city"].Value),
            slug.Groups["kind"].Value == "highest" ? TemperatureKind.Max : TemperatureKind.Min,
            date,
            unit,
            sourceKind,
            stationId,
            ev.CreatedAt,
            ev.EndDate,
            ev.Closed,
            ev.Volume,
            ev.Description,
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
        if (m.Groups["cmp"].Value == "<")
            upper = a - 1;
        else if (m.Groups["cmp"].Value == ">")
            lower = a + 1;
        else if (tail is "or below" or "or lower")
            upper = a;
        else if (tail is "or higher" or "or above")
            lower = a;
        else if (m.Groups["b"].Success)
            (lower, upper) = (a, ParseInt(m.Groups["b"].Value));
        else
            lower = upper = a;
        return true;
    }

    private static bool TryParseBucket(GammaMarket m, out RangeBucket? bucket, out string? error)
    {
        bucket = null;
        var label = m.GroupItemTitle ?? "";
        if (!TryParseRange(label, out var lower, out var upper))
        {
            error = $"cannot parse bucket '{label}'";
            return false;
        }

        return RangeBucketFactory.TryCreate(m, label, lower, upper, out bucket, out error);
    }

    private static (ResolutionSourceKind, string?) ParseSource(string description, string? resolutionSource)
    {
        var text = $"{resolutionSource} {description}";
        var noaa = NoaaSiteRegex().Match(text);
        if (noaa.Success)
            return (ResolutionSourceKind.NoaaTimeseries, noaa.Groups["id"].Value.ToUpperInvariant());

        var wu = WundergroundStationRegex().Match(text);
        if (wu.Success)
            return (ResolutionSourceKind.WeatherUnderground, wu.Groups["id"].Value);

        if (description.Contains("Hong Kong Observatory", StringComparison.OrdinalIgnoreCase))
            return (ResolutionSourceKind.HongKongObservatory, "HKO");

        return (ResolutionSourceKind.Unknown, null);
    }

    private static bool TryParseDate(Match slug, DateTimeOffset? anchor, out DateOnly date)
    {
        date = default;
        var monthText = slug.Groups["month"].Value;
        var month = Array.FindIndex(MonthPrefixes, p => monthText.StartsWith(p, StringComparison.Ordinal)) + 1;
        if (month == 0)
            return false;

        var day = int.Parse(slug.Groups["day"].Value, CultureInfo.InvariantCulture);
        if (anchor is null)
            return slug.Groups["year"].Success
                   && TryCreateDate(int.Parse(slug.Groups["year"].Value, CultureInfo.InvariantCulture), month, day, out date);

        // The event is created a few days before its date, so the anchor's year is the reliable one
        // (slugs may lack a year or carry a typo like "jan-26-2024" for a 2025 event);
        // roll over for late-December listings of January days.
        var anchorDate = DateOnly.FromDateTime(anchor.Value.UtcDateTime);
        if (!TryCreateDate(anchor.Value.Year, month, day, out date))
            return false;
        if (date < anchorDate.AddDays(-7))
            return TryCreateDate(anchor.Value.Year + 1, month, day, out date);
        return true;
    }

    private static bool TryCreateDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (day > DateTime.DaysInMonth(year, month))
            return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    private static int ParseInt(string s) =>
        int.Parse(s.Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
