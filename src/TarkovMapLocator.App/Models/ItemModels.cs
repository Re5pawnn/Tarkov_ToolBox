using System.Text.Json.Serialization;

namespace TarkovMapLocator.App.Models;

public sealed record MarketStateSnapshot(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("updatedAt")] double? UpdatedAt,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("counts")] MarketModeCounts? Counts);

public sealed record FleaMarketSearchResult(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("updatedAt")] double? UpdatedAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<FleaMarketItem> Items);

public sealed record FleaMarketItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nameZh")] string NameZh,
    [property: JsonPropertyName("shortNameZh")] string ShortNameZh,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shortName")] string ShortName,
    [property: JsonPropertyName("fleaPrice")] long? FleaPrice,
    [property: JsonPropertyName("avg24hPrice")] long? Avg24hPrice,
    [property: JsonPropertyName("low24hPrice")] long? Low24hPrice,
    [property: JsonPropertyName("high24hPrice")] long? High24hPrice,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("types")] IReadOnlyList<string>? Types,
    [property: JsonPropertyName("bestTrader")] MarketTraderOffer? BestTrader);

public sealed record MarketTraderOffer(
    [property: JsonPropertyName("price")] long? Price,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("vendorName")] string VendorName);

public sealed record MarketModeCounts(
    [property: JsonPropertyName("pvp")] int Pvp,
    [property: JsonPropertyName("pve")] int Pve,
    [property: JsonPropertyName("pvpFlea")] int PvpFlea,
    [property: JsonPropertyName("pveFlea")] int PveFlea);
