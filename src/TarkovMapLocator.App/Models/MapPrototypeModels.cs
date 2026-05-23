namespace TarkovMapLocator.App.Models;

using TarkovMapLocator.Core.Maps;

public sealed record MapPrototypePoint(
    string Label,
    double U,
    double V,
    string Kind,
    double YawDegrees = 0,
    string IconName = "",
    bool ShowLabel = false,
    string LabelText = "")
    : MapDisplayPoint(Label, U, V, Kind, YawDegrees);

public sealed record NativePeerMarker(
    string PeerId,
    string DisplayName,
    string Color,
    double U,
    double V,
    double YawDegrees,
    DateTimeOffset LastSeenAt);

public sealed record NativeMapPipPreferences(
    bool IsEnabled,
    double Opacity,
    double Zoom,
    int Left,
    int Top,
    int Width,
    int Height);

public sealed record NativeMapPipSnapshot(
    MapPrototypeModel Map,
    MapPrototypePoint? PlayerPoint);

public sealed record NativePeerListItem(
    string DisplayName,
    string RoleText,
    string Color,
    string MapName,
    string LastSeenText,
    bool IsVisibleOnCurrentMap,
    bool HasPosition);

public sealed record NativeLanSyncConfig(
    string DisplayName,
    string Color,
    string Mode,
    string RemoteEndpoint);

public sealed record NativeLanSyncRequest(
    string DisplayName,
    string Color,
    string Mode,
    string RemoteEndpoint);

public sealed record NativeLanSyncState(
    string Mode,
    string Status,
    bool IsRunning,
    string LocalEndpoint,
    string RemoteEndpoint,
    string LastError,
    string ConnectionHint,
    IReadOnlyList<NativePeerListItem> Peers);

public sealed record MapPrototypePoi(
    string Label,
    double X,
    double Z,
    string Kind,
    string Source,
    string IconName = "",
    bool ShowLabel = false,
    string LabelText = "");

public sealed record PoiFilterOption(
    string Kind,
    string Label,
    bool IsEnabled,
    int Count);

public sealed record MapBaseOption(
    string Value,
    string Label);

public static class NativeMapBaseValues
{
    public const string Cache = "cache";
    public const string Svg = "svg";
}

public static class NativeMapLayerValues
{
    public const string Auto = "__AUTO_LAYER__";
    public const string Surface = "__SURFACE__";
    public const string All = "all";
    public const string Main = "main";
    public const string Extracts = "extracts";
    public const string Spawns = "spawns";
    public const string Objects = "objects";
}

public sealed record MapProjectionResult(
    bool HasCoordinate,
    bool IsInBounds,
    MapPrototypePoint? PlayerPoint,
    string StatusMessage,
    MapPrototypeModel? SuggestedMap = null,
    ScreenshotCoordinate? Coordinate = null);

public sealed record MapCandidateResult(
    bool HasCoordinate,
    IReadOnlyList<MapPrototypeMetadata> CandidateMaps,
    ScreenshotCoordinate? Coordinate,
    string StatusMessage);

public sealed record MapCachePreparationResult(
    int RequestedCount,
    int PreparedCount,
    IReadOnlyList<string> PreparedMapNames,
    IReadOnlyList<string> FailedMapNames,
    string StatusMessage);

public sealed record MapCacheCheckResult(
    int ReadyCount,
    int FailedCount,
    bool HasMissingOrExpired,
    string StatusMessage);

public sealed record MapPrototypeMetadata(
    string Id,
    string Key,
    string Name,
    string NormalizedName,
    string NameId,
    double X0,
    double Z0,
    double X1,
    double Z1,
    bool ReverseCoordinate,
    string SvgPath,
    string TilePath,
    string? SvgImagePath,
    string? LocalImagePath,
    MapHeightRange? HeightRange,
    IReadOnlyList<MapLayerMetadata> Layers,
    IReadOnlyList<MapPrototypePoi> Pois,
    IReadOnlyList<string> PoiSources)
{
    public bool HasLocalImage => !string.IsNullOrWhiteSpace(LocalImagePath);

    public double Area => Math.Abs((X1 - X0) * (Z1 - Z0));

    public MapBounds Bounds => new(X0, Z0, X1, Z1, ReverseCoordinate);
}

public sealed class MapPrototypeModel
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Key { get; init; } = "";

    public string NormalizedName { get; init; } = "";

    public string NameId { get; init; } = "";

    public string ImagePath { get; init; } = "";

    public string MapBase { get; init; } = NativeMapBaseValues.Cache;

    public string MapLayer { get; init; } = NativeMapLayerValues.Surface;

    public MapHeightRange? HeightRange { get; init; }

    public IReadOnlyList<MapLayerMetadata> Layers { get; init; } = [];

    public double MinX { get; init; }

    public double MinZ { get; init; }

    public double MaxX { get; init; }

    public double MaxZ { get; init; }

    public bool ReverseCoordinate { get; init; }

    public double AspectRatio { get; init; } = 16.0 / 9.0;

    public IReadOnlyList<MapPrototypePoint> Points { get; init; } = [];

    public string PoiSource { get; init; } = "extracts";

    public IReadOnlyDictionary<string, int> PoiKindCounts { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public MapPrototypePoint PlayerPoint { get; init; } = new("Player", 0.5, 0.5, "player", 45);

    public MapBounds Bounds => new(MinX, MinZ, MaxX, MaxZ, ReverseCoordinate);
}
