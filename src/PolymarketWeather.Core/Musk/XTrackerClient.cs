using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PolymarketWeather.Core.Musk;

public sealed record TrackedPost(string PlatformId, string Handle, string? Content, DateTimeOffset CreatedAt, DateTimeOffset? ImportedAt);

public sealed record Tracking(string Id, string Handle, string Title, DateTimeOffset Start, DateTimeOffset End, string? MarketLink, bool IsActive);

/// <summary>
/// Polymarket's post counter (xtracker.polymarket.com), the resolution source of the post-count markets.
/// Unofficial API taken from the site's own front end; it may change without notice.
/// </summary>
public sealed class XTrackerClient(HttpClient http)
{
    public const string DefaultBaseAddress = "https://xtracker.polymarket.com/";

    public async Task<IReadOnlyList<TrackedPost>> GetPostsAsync(string handle, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var url = $"api/users/{Uri.EscapeDataString(handle)}/posts?startDate={from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&endDate={to.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        var response = await http.GetFromJsonAsync<Envelope<List<PostRow>>>(url, ct);
        return response?.Data?.Select(p => new TrackedPost(p.PlatformId, handle, p.Content, p.CreatedAt, p.ImportedAt)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<Tracking>> GetTrackingsAsync(string handle, CancellationToken ct = default)
    {
        var response = await http.GetFromJsonAsync<Envelope<UserRow>>($"api/users/{Uri.EscapeDataString(handle)}", ct);
        return response?.Data?.Trackings.Select(t => new Tracking(t.Id, handle, t.Title, t.StartDate, t.EndDate, t.MarketLink, t.IsActive)).ToList() ?? [];
    }

    private sealed class Envelope<T>
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("data")] public T? Data { get; init; }
    }

    private sealed class PostRow
    {
        [JsonPropertyName("platformId")] public string PlatformId { get; init; } = "";
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("importedAt")] public DateTimeOffset? ImportedAt { get; init; }
    }

    private sealed class UserRow
    {
        [JsonPropertyName("trackings")] public List<TrackingRow> Trackings { get; init; } = [];
    }

    private sealed class TrackingRow
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("startDate")] public DateTimeOffset StartDate { get; init; }
        [JsonPropertyName("endDate")] public DateTimeOffset EndDate { get; init; }
        [JsonPropertyName("marketLink")] public string? MarketLink { get; init; }
        [JsonPropertyName("isActive")] public bool IsActive { get; init; }
    }
}
