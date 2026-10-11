using System.Net.Http.Json;

namespace PolymarketWeather.Core.Polymarket.Gamma;

public sealed class GammaClient(HttpClient http)
{
    public const string DefaultBaseAddress = "https://gamma-api.polymarket.com/";
    public const string DailyTemperatureTag = "daily-temperature";

    private const int EventPageSize = 100;
    private const int SeriesPageSize = 50;

    public Task<IReadOnlyList<GammaEvent>> GetOpenEventsByTagAsync(string tagSlug, CancellationToken ct = default) =>
        GetPagedAsync<GammaEvent>($"events?closed=false&tag_slug={Uri.EscapeDataString(tagSlug)}", EventPageSize, ct);

    public Task<IReadOnlyList<GammaEvent>> GetEventsBySeriesAsync(string seriesId, bool closed, CancellationToken ct = default) =>
        GetPagedAsync<GammaEvent>(
            $"events?series_id={Uri.EscapeDataString(seriesId)}&closed={(closed ? "true" : "false")}&order=endDate&ascending=true",
            EventPageSize,
            ct);

    public Task<IReadOnlyList<GammaSeries>> GetAllSeriesAsync(CancellationToken ct = default) =>
        GetPagedAsync<GammaSeries>("series?", SeriesPageSize, ct);

    public Task<GammaEvent?> GetEventBySlugAsync(string slug, CancellationToken ct = default) =>
        http.GetFromJsonAsync<GammaEvent>($"events/slug/{Uri.EscapeDataString(slug)}", ct);

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(string query, int pageSize, CancellationToken ct)
    {
        var separator = query.EndsWith('?') ? "" : "&";
        var result = new List<T>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await http.GetFromJsonAsync<List<T>>($"{query}{separator}limit={pageSize}&offset={offset}", ct) ?? [];
            result.AddRange(page);
            if (page.Count < pageSize)
                return result;
        }
    }
}
