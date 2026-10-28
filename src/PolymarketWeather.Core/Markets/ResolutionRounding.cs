namespace PolymarketWeather.Core.Markets;

/// <summary>
/// Turns a METAR temperature (°C, tenths when the station reports the T group) into the whole number that picks
/// the bucket: converted to °F for Fahrenheit markets, then rounded half up, as in the rules published in October 2026.
/// </summary>
public static class ResolutionRounding
{
    public static int ToBucketValue(double tempC, TemperatureUnit unit)
    {
        // decimal keeps values such as 16.5 or -8.5 exactly on the half before rounding.
        var c = (decimal)tempC;
        var value = unit == TemperatureUnit.Fahrenheit ? c * 9 / 5 + 32 : c;
        return (int)Math.Floor(value + 0.5m);
    }
}
