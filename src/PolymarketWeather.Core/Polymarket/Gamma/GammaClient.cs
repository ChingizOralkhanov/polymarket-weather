using System.Net.Http.Json;

namespace PolymarketWeather.Core.Polymarket.Gamma;

public sealed class GammaClient(HttpClient http)
{
    public const string DefaultBaseAddress = "https://gamma-api.polymarket.com/";
    public const string DailyTemperatureTag = "daily-temperature";

    private const int PageSize = 100;

    public async Task<IReadOnlyList<GammaEvent>> GetOpenEventsByTagAsync(string tagSlug, CancellationToken ct = default)
    {
        var result = new List<GammaEvent>();
        for (var offset = 0; ; offset += PageSize)
        {
            var url = $"events?closed=false&limit={PageSize}&offset={offset}&tag_slug={Uri.EscapeDataString(tagSlug)}";
            var page = await http.GetFromJsonAsync<List<GammaEvent>>(url, ct) ?? [];
            result.AddRange(page);
            if (page.Count < PageSize)
                return result;
        }
    }

    public Task<GammaEvent?> GetEventBySlugAsync(string slug, CancellationToken ct = default) =>
        http.GetFromJsonAsync<GammaEvent>($"events/slug/{Uri.EscapeDataString(slug)}", ct);
}
