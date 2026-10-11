using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Core.Tests;

public class TemperatureMarketParserTests
{
    [Theory]
    [InlineData("61°F or below", null, 61)]
    [InlineData("62-63°F", 62, 63)]
    [InlineData("80°F or higher", 80, null)]
    [InlineData("13°C", 13, 13)]
    [InlineData("-2°C", -2, -2)]
    [InlineData("-5°C or below", null, -5)]
    [InlineData("-1-0°F", -1, 0)]
    [InlineData("67–68°F", 67, 68)]
    [InlineData("<29°F", null, 28)]
    [InlineData("57 or higher", 57, null)]
    public void ParsesBucketLabels(string label, int? lower, int? upper)
    {
        Assert.True(TemperatureMarketParser.TryParseRange(label, out var lo, out var hi));
        Assert.Equal(lower, lo);
        Assert.Equal(upper, hi);
    }

    [Fact]
    public void ParsesNoaaFahrenheitEvent()
    {
        var ev = new GammaEvent
        {
            Id = "1163283",
            Slug = "highest-temperature-in-nyc-on-october-12-2026",
            Description = "This market will resolve to the temperature range that contains the highest temperature recorded by NOAA at the LaGuardia Airport Station in degrees Fahrenheit on 12 Oct '26.",
            ResolutionSource = "https://www.weather.gov/wrh/timeseries?site=klga",
            Markets =
            [
                Market("2", "62-63°F"),
                Market("1", "61°F or below"),
            ],
        };

        Assert.True(TemperatureMarketParser.TryParse(ev, out var te, out var error), error);
        Assert.Equal("nyc", te!.City);
        Assert.Equal(TemperatureKind.Max, te.Kind);
        Assert.Equal(new DateOnly(2026, 10, 12), te.Date);
        Assert.Equal(TemperatureUnit.Fahrenheit, te.Unit);
        Assert.Equal(ResolutionSourceKind.NoaaTimeseries, te.SourceKind);
        Assert.Equal("KLGA", te.StationId);
        Assert.Equal(["61°F or below", "62-63°F"], te.Buckets.Select(b => b.Label));
        Assert.Equal("yes-1", te.Buckets[0].YesTokenId);
        Assert.Equal("no-1", te.Buckets[0].NoTokenId);
        Assert.Equal(BucketOutcome.Unresolved, te.Buckets[0].Outcome);
    }

    [Fact]
    public void ParsesWundergroundCelsiusMinEvent()
    {
        var ev = new GammaEvent
        {
            Slug = "lowest-temperature-in-taipei-on-october-12-2026",
            Description = "This market will resolve based on the lowest temperature recorded in the 'Daily Observations' table on Weather Underground ... in degrees Celsius ... https://www.wunderground.com/history/daily/tw/taipei/RCSS.\n\nIn",
            Markets = [Market("0", "23°C or below")],
        };

        Assert.True(TemperatureMarketParser.TryParse(ev, out var te, out var error), error);
        Assert.Equal(TemperatureKind.Min, te!.Kind);
        Assert.Equal(TemperatureUnit.Celsius, te.Unit);
        Assert.Equal(ResolutionSourceKind.WeatherUnderground, te.SourceKind);
        Assert.Equal("RCSS", te.StationId);
    }

    private static GammaMarket Market(string threshold, string label) => new()
    {
        Id = threshold,
        GroupItemThreshold = threshold,
        GroupItemTitle = label,
        OutcomesJson = "[\"Yes\", \"No\"]",
        ClobTokenIdsJson = $"[\"yes-{threshold}\", \"no-{threshold}\"]",
    };
}

public class TemperatureMarketParserLegacyTests
{
    [Theory]
    [InlineData("highest-temperature-in-nyc-on-jan-22", "2025-01-21T16:21:16Z", 2025, 1, 22)]
    [InlineData("highest-temperature-in-nyc-on-jan-1", "2025-12-30T10:00:00Z", 2026, 1, 1)]
    [InlineData("arch-highest-temperature-in-nyc-on-may-19-2026", "2026-05-17T04:03:57Z", 2026, 5, 19)]
    [InlineData("arch-arch-highest-temperature-in-nyc-on-may-16-2026", "2026-05-14T04:00:00Z", 2026, 5, 16)]
    [InlineData("highest-temperature-in-nyc-on-jan-26-2024", "2025-01-25T16:07:35Z", 2025, 1, 26)]
    [InlineData("highest-temperature-in-nyc-on-may-8-625", "2025-05-07T07:52:33Z", 2025, 5, 8)]
    [InlineData("lowest-temperature-in-panama-city-on-september-30-2026", "2026-09-28T12:00:00Z", 2026, 9, 30)]
    public void ParsesLegacySlugs(string slug, string createdAt, int y, int m, int d)
    {
        var ev = new GammaEvent
        {
            Slug = slug,
            CreatedAt = DateTimeOffset.Parse(createdAt),
            Description = "... from Wunderground ... https://www.wunderground.com/history/daily/us/ny/new-york-city/KLGA.",
            Markets = [],
        };

        Assert.True(TemperatureMarketParser.TryParse(ev, out var te, out var error), error);
        Assert.Equal(new DateOnly(y, m, d), te!.Date);
        Assert.Equal(ResolutionSourceKind.WeatherUnderground, te.SourceKind);
        Assert.Equal("KLGA", te.StationId);
    }

    [Fact]
    public void ReadsResolvedOutcomeAndClosedTime()
    {
        var ev = new GammaEvent
        {
            Slug = "highest-temperature-in-nyc-on-jan-22",
            EndDate = DateTimeOffset.Parse("2025-01-22T12:00:00Z"),
            Markets =
            [
                new GammaMarket
                {
                    Id = "1", GroupItemThreshold = "0", GroupItemTitle = "16°F or lower",
                    OutcomesJson = "[\"Yes\", \"No\"]", ClobTokenIdsJson = "[\"y\", \"n\"]",
                    OutcomePricesJson = "[\"1\", \"0\"]", UmaResolutionStatus = "resolved",
                    ClosedTime = "2025-01-22 20:37:40+00",
                },
            ],
        };

        Assert.True(TemperatureMarketParser.TryParse(ev, out var te, out var error), error);
        var bucket = te!.Buckets.Single();
        Assert.Equal(16, bucket.Upper);
        Assert.Equal(BucketOutcome.Yes, bucket.Outcome);
        Assert.Equal(new DateTimeOffset(2025, 1, 22, 20, 37, 40, TimeSpan.Zero), bucket.ClosedTime);
        Assert.Same(bucket, te.WinningBucket);
    }
}

public class TemperatureMarketParserSourceTests
{
    [Fact]
    public void ParsesWundergroundWeatherUrlAndCityAlias()
    {
        var ev = new GammaEvent
        {
            Slug = "highest-temperature-in-new-york-city-on-december-4",
            CreatedAt = DateTimeOffset.Parse("2025-12-02T10:00:00Z"),
            Description = "... information from Wunderground ... available here: https://www.wunderground.com/weather/KORD.",
            Markets = [],
        };

        Assert.True(TemperatureMarketParser.TryParse(ev, out var te, out var error), error);
        Assert.Equal("nyc", te!.City);
        Assert.Equal(ResolutionSourceKind.WeatherUnderground, te.SourceKind);
        Assert.Equal("KORD", te.StationId);
    }
}
