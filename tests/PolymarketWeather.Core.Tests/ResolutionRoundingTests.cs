using PolymarketWeather.Core.Markets;

namespace PolymarketWeather.Core.Tests;

public class ResolutionRoundingTests
{
    // Examples quoted in the market rules.
    [Theory]
    [InlineData(13.9, 14)]
    [InlineData(17.4, 17)]
    [InlineData(16.5, 17)]
    [InlineData(-1.7, -2)]
    [InlineData(-8.5, -8)]
    public void RoundsCelsiusHalfUp(double tempC, int expected) =>
        Assert.Equal(expected, ResolutionRounding.ToBucketValue(tempC, TemperatureUnit.Celsius));

    [Theory]
    [InlineData(18.3, 65)]
    [InlineData(17.8, 64)]
    [InlineData(19.0, 66)]
    [InlineData(-22.5, -8)]
    public void ConvertsToFahrenheitThenRoundsHalfUp(double tempC, int expected) =>
        Assert.Equal(expected, ResolutionRounding.ToBucketValue(tempC, TemperatureUnit.Fahrenheit));
}
