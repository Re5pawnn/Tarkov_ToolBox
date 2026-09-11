namespace TarkovMapLocatorDesktop.Models;

public sealed record MarketSellOffer(int Price, string Source, string VendorName);

public sealed record MarketBuyOffer(int Price, string VendorName, int MinTraderLevel);

public sealed record MarketItem(
    string Id,
    string NameZh,
    string ShortNameZh,
    string Name,
    string ShortName,
    int? Avg24hPrice,
    int? Low24hPrice,
    int? High24hPrice,
    int? LastLowPrice,
    int? FleaPrice,
    MarketSellOffer? BestTrader,
    IReadOnlyList<MarketSellOffer> SellFor,
    string IconLink = "",
    string GridImageLink = "",
    IReadOnlyList<string>? Types = null,
    int? Width = null,
    int? Height = null,
    int? BasePrice = null,
    MarketBuyOffer? BestTraderBuy = null);

public sealed record MarketCatalog(
    IReadOnlyList<MarketItem> PvpItems,
    IReadOnlyList<MarketItem> PveItems,
    DateTimeOffset? UpdatedAt,
    string Source,
    string? ErrorMessage,
    IReadOnlyList<MarketItem>? PvpSeasonItems = null)
{
    /// <summary>All three independent economies are present and fresh.</summary>
    public bool IsComplete => PvpItems.Count > 0 && PveItems.Count > 0 && PvpSeasonItems is { Count: > 0 };

    /// <summary>At least one economy can still be shown while a complete refresh is retried.</summary>
    public bool IsAvailable => PvpItems.Count > 0 || PveItems.Count > 0 || PvpSeasonItems is { Count: > 0 };
    public bool IsStale => UpdatedAt is null || DateTimeOffset.Now - UpdatedAt > TimeSpan.FromHours(12);
    public IReadOnlyList<MarketItem> GetItems(string mode) =>
        string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? PveItems :
        string.Equals(mode, "pvp-season", StringComparison.OrdinalIgnoreCase) ? PvpSeasonItems ?? [] :
        PvpItems;
}

/// <summary>One original tracker requirement source (task or hideout upgrade).</summary>
public sealed record TrackerRequirementSource(
    string Key,
    string Type,
    string Name,
    string Detail,
    int Count,
    bool FoundInRaid,
    string Trader,
    bool Completed,
    bool Choice);

/// <summary>Aggregated tracker row, matching the original tool's task-item view.</summary>
public sealed record TrackerItem(
    string Id,
    string Name,
    string ShortName,
    string IconLink,
    int Required,
    int CompletedRequired,
    int CompletedTaskRequired,
    int CompletedHideoutRequired,
    int FoundInRaidRequired,
    int TaskRequired,
    int HideoutRequired,
    int Have,
    int? FleaPrice,
    int? Avg24hPrice,
    IReadOnlyList<TrackerRequirementSource> Sources,
    bool Choice,
    IReadOnlyList<string> Choices)
{
    public int Remaining => Math.Max(0, Required - Have);
    public bool Completed => Required <= 0 && CompletedRequired > 0;
    public long? EstimatedCost
    {
        get
        {
            var price = FleaPrice ?? Avg24hPrice;
            return price is > 0 ? (long)Remaining * price.Value : null;
        }
    }
}

public sealed record TaskTrackerLoadResult(
    IReadOnlyList<TrackerItem> Items,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage,
    bool IsStale,
    long TotalRequired,
    long TotalRemaining,
    long TotalEstimatedCost,
    long StateVersion)
{
    public bool IsAvailable => ErrorMessage is null || Items.Count > 0;
}

/// <summary>Lightweight cache state used to decide whether a background refresh is needed.</summary>
public sealed record TaskTrackerCacheStatus(bool IsAvailable, bool IsStale);

public sealed record HideoutStationLevel(
    string Id,
    string Name,
    int CurrentLevel,
    int MaxLevel);
