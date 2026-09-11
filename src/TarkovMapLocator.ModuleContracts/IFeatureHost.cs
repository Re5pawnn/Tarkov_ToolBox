namespace TarkovMapLocator.ModuleContracts;

public interface IFeatureHost
{
    string DataDirectory { get; }

    string CurrentGameMode { get; }

    string CurrentMarketMode { get; }

    event EventHandler? MarketDataChanged;

    CancellationToken ShutdownToken { get; }

    void Navigate(string route);

    bool NavigateBack();

    void ShowNotification(string message);

    void WriteLog(FeatureLogLevel level, string category, string message, string? details = null);

    IReadOnlyDictionary<string, FeatureMarketItem> GetMarketItems(string mode);

    FeatureMarketSnapshot GetMarketSnapshot(string mode);

    void SetMarketMode(string mode);

    Task EnsureMarketFreshAsync(CancellationToken cancellationToken = default);

    Task RefreshMarketAsync(CancellationToken cancellationToken = default);

    FeatureTaskTrackerSnapshot GetTaskItems(
        string mode,
        string query,
        bool includeTasks,
        bool includeHideout,
        bool showCompleted);

    Task EnsureTaskItemsFreshAsync(CancellationToken cancellationToken = default);

    Task RefreshTaskItemsAsync(CancellationToken cancellationToken = default);

    void SetTaskItemCount(string mode, string itemId, int have);

    void SetTaskItemSourceCompleted(string mode, string sourceKey, bool completed);

    IReadOnlyList<FeatureHideoutStation> GetHideoutStations(string mode);

    void SetHideoutLevels(string mode, IReadOnlyDictionary<string, int> levels);

    void ApplyTaskTrackingPinSelection(bool enabled, IReadOnlyList<string> taskNames);

    FeatureSharedLocation GetSharedLocation();

    FeatureMobileMapSnapshot GetMobileMapSnapshot();

    int EnsureMobileMapServerRunning();

    string? HandleTeamSyncRequest(string requestJson, string remoteAddress);

    Task StopHostedTeamSyncAsync();

    void RefreshMapMarkers();

    Task<System.Windows.Media.ImageSource?> LoadItemIconAsync(
        string itemId,
        string? iconLink,
        string? fallbackIconLink = null,
        bool preferFullImage = false);

    Task<System.Windows.Media.ImageSource?> LoadImageAsync(
        string imageId,
        string imageUrl,
        bool fullSize = false);
}

public enum FeatureLogLevel { Trace, Info, Warning, Error }

public sealed record FeatureTraderOffer(int Price, string VendorName, int MinTraderLevel = 0);

public sealed record FeatureMarketSellOffer(int Price, string Source, string VendorName);

public sealed record FeatureMarketItem(
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
    FeatureTraderOffer? BestTrader,
    IReadOnlyList<FeatureMarketSellOffer> SellFor,
    string IconLink,
    string GridImageLink,
    IReadOnlyList<string> Types,
    int? BasePrice,
    FeatureTraderOffer? BestTraderBuy,
    int? Width = 1,
    int? Height = 1);

public sealed record FeatureMarketSnapshot(
    IReadOnlyList<FeatureMarketItem> Items,
    DateTimeOffset? UpdatedAt,
    string Source,
    string? ErrorMessage,
    bool IsStale,
    bool IsAvailable);

public sealed record FeatureTrackerRequirementSource(
    string Key,
    string Type,
    string Name,
    string Detail,
    int Count,
    bool FoundInRaid,
    string Trader,
    bool Completed,
    bool Choice);

public sealed record FeatureTrackerItem(
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
    IReadOnlyList<FeatureTrackerRequirementSource> Sources,
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

public sealed record FeatureTaskTrackerSnapshot(
    IReadOnlyList<FeatureTrackerItem> Items,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage,
    bool IsStale,
    long TotalRequired,
    long TotalRemaining,
    long TotalEstimatedCost,
    long StateVersion);

public sealed record FeatureHideoutStation(
    string Id,
    string Name,
    int CurrentLevel,
    int MaxLevel);

public sealed record FeatureSharedLocation(
    string? MapId,
    string? MapName,
    double? X,
    double? Y,
    double? Z,
    double? YawDegrees);

public sealed record FeatureTeamPeerPosition(
    string PeerId,
    string DisplayName,
    string Color,
    string MapId,
    double WorldX,
    double? WorldHeight,
    double WorldZ,
    double YawDegrees,
    DateTimeOffset LastSeenAt);

public sealed record FeatureMobileMapAsset(
    string Key,
    string FilePath,
    string ContentType);

public sealed record FeatureMobileMapMarker(
    string Id,
    string Type,
    string Label,
    double X,
    double Y,
    double? HeadingDegrees,
    string? ColorHex);

public sealed record FeatureMobileMapSnapshot(
    string? MapId,
    string? MapName,
    string MapStyle,
    string? LayerId,
    string LayerName,
    FeatureMobileMapAsset? BaseMap,
    FeatureMobileMapAsset? LayerMap,
    bool LayerDimsBaseMap,
    bool IsListening,
    bool HasLivePosition,
    bool IsPositionStale,
    DateTimeOffset? PositionUpdatedAt,
    IReadOnlyList<FeatureMobileMapMarker> Markers)
{
    public FeatureMobileMapAsset? CompactBaseMap { get; init; }

    public FeatureMobileMapAsset? SharpBaseMap { get; init; }

    public FeatureMobileMapAsset? CompactLayerMap { get; init; }

    public FeatureMobileMapAsset? SharpLayerMap { get; init; }
}
