using System.Text.Json.Serialization;

namespace PolymarketWeather.Core.Polymarket.Gamma;

public sealed class GammaEvent
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("resolutionSource")] public string? ResolutionSource { get; init; }
    [JsonPropertyName("startDate")] public DateTimeOffset? StartDate { get; init; }
    [JsonPropertyName("endDate")] public DateTimeOffset? EndDate { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("closed")] public bool Closed { get; init; }
    [JsonPropertyName("negRisk")] public bool NegRisk { get; init; }
    [JsonPropertyName("liquidity")] public decimal? Liquidity { get; init; }
    [JsonPropertyName("volume")] public decimal? Volume { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; init; }
    [JsonPropertyName("markets")] public List<GammaMarket> Markets { get; init; } = [];
    [JsonPropertyName("series")] public List<GammaSeries> Series { get; init; } = [];
}

public sealed class GammaSeries
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("recurrence")] public string? Recurrence { get; init; }
    [JsonPropertyName("closed")] public bool Closed { get; init; }
}

public sealed class GammaMarket
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("question")] public string Question { get; init; } = "";
    [JsonPropertyName("conditionId")] public string ConditionId { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("groupItemTitle")] public string? GroupItemTitle { get; init; }
    [JsonPropertyName("groupItemThreshold")] public string? GroupItemThreshold { get; init; }
    [JsonPropertyName("resolutionSource")] public string? ResolutionSource { get; init; }
    [JsonPropertyName("endDate")] public DateTimeOffset? EndDate { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("closed")] public bool Closed { get; init; }

    // Gamma returns these two as JSON-encoded strings, e.g. "[\"Yes\", \"No\"]".
    [JsonPropertyName("outcomes")] public string? OutcomesJson { get; init; }
    [JsonPropertyName("clobTokenIds")] public string? ClobTokenIdsJson { get; init; }
    [JsonPropertyName("outcomePrices")] public string? OutcomePricesJson { get; init; }

    [JsonPropertyName("umaResolutionStatus")] public string? UmaResolutionStatus { get; init; }

    // Not ISO 8601: "2025-01-22 20:37:40+00".
    [JsonPropertyName("closedTime")] public string? ClosedTime { get; init; }

    [JsonPropertyName("orderPriceMinTickSize")] public decimal? TickSize { get; init; }
    [JsonPropertyName("orderMinSize")] public decimal? MinOrderSize { get; init; }
}
