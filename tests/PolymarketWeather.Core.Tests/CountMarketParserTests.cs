using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Polymarket.Gamma;

namespace PolymarketWeather.Core.Tests;

public class CountMarketParserTests
{
    [Theory]
    [InlineData("<20", null, 19)]
    [InlineData("20-39", 20, 39)]
    [InlineData("500+", 500, null)]
    [InlineData("0-39", 0, 39)]
    public void ParsesBucketLabels(string label, int? lower, int? upper)
    {
        Assert.True(CountMarketParser.TryParseRange(label, out var lo, out var hi));
        Assert.Equal(lower, lo);
        Assert.Equal(upper, hi);
    }

    [Theory]
    [InlineData("20°F")]
    [InlineData("abc")]
    public void RejectsOtherLabels(string label) =>
        Assert.False(CountMarketParser.TryParseRange(label, out _, out _));

    [Fact]
    public void ParsesEventInThresholdOrder()
    {
        var ev = new GammaEvent
        {
            Id = "1",
            Slug = "elon-musk-of-tweets-october-13-october-20-2026",
            Title = "Elon Musk # tweets October 13 - October 20, 2026?",
            Series = [new GammaSeries { Id = "10000", Slug = "elon-tweets" }],
            Markets =
            [
                Market("1", "20-39", "[\"0\", \"1\"]"),
                Market("0", "<20", "[\"1\", \"0\"]"),
                Market("2", "40+", "[\"0\", \"1\"]"),
            ],
        };

        Assert.True(CountMarketParser.TryParse(ev, out var ce, out var error), error);
        Assert.Equal("10000", ce!.SeriesId);
        Assert.Equal(["<20", "20-39", "40+"], ce.Buckets.Select(b => b.Label));
        Assert.Equal("<20", ce.WinningBucket!.Label);
    }

    private static GammaMarket Market(string threshold, string label, string prices) => new()
    {
        Id = threshold,
        GroupItemThreshold = threshold,
        GroupItemTitle = label,
        OutcomesJson = "[\"Yes\", \"No\"]",
        ClobTokenIdsJson = $"[\"yes-{threshold}\", \"no-{threshold}\"]",
        OutcomePricesJson = prices,
        UmaResolutionStatus = "resolved",
    };
}
