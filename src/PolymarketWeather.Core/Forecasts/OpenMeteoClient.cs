using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace PolymarketWeather.Core.Forecasts;

public sealed record HourlyForecast(DateTimeOffset Time, double? TempC);

public sealed class OpenMeteoClient(HttpClient http)
{
    public const string PreviousRunsBaseAddress = "https://previous-runs-api.open-meteo.com/";

    /// <summary>
    /// Hourly 2 m temperature of one model as it was forecast <c>lead</c> days before each hour (0 = latest run).
    /// Times are requested in UTC: Open-Meteo applies the current UTC offset to the whole range, which is wrong across DST.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<HourlyForecast>>> GetPreviousRunsAsync(
        double latitude, double longitude, string model, IReadOnlyList<int> leads, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var variables = leads.Select(VariableName).ToList();
        var url = "v1/forecast"
                  + $"?latitude={latitude.ToString(CultureInfo.InvariantCulture)}&longitude={longitude.ToString(CultureInfo.InvariantCulture)}"
                  + $"&hourly={string.Join(',', variables)}&models={Uri.EscapeDataString(model)}"
                  + $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}&timezone=UTC";

        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Open-Meteo {(int)response.StatusCode}: {Truncate(body)}", null, response.StatusCode);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Overload and quota errors sometimes come back as plain text with a 200.
            throw new HttpRequestException($"Open-Meteo returned non-JSON: {Truncate(body)}");
        }
        using var _ = doc;
        var hourly = doc.RootElement.GetProperty("hourly");
        var times = hourly.GetProperty("time").EnumerateArray()
            .Select(t => new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture), DateTimeKind.Utc)))
            .ToList();

        var result = new Dictionary<int, IReadOnlyList<HourlyForecast>>();
        for (var i = 0; i < leads.Count; i++)
        {
            if (!hourly.TryGetProperty(variables[i], out var values))
                continue;
            result[leads[i]] = values.EnumerateArray()
                .Select((v, j) => new HourlyForecast(times[j], v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null))
                .ToList();
        }
        return result;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    private static string VariableName(int lead) => lead == 0 ? "temperature_2m" : $"temperature_2m_previous_day{lead}";
}
