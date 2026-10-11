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
