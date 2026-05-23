using TarkovMapLocator.App.Models;
using TarkovMapLocator.App.Services;
using TarkovMapLocator.Core.Maps;

namespace TarkovMapLocator.App.ViewModels;

public sealed class MapPrototypeViewModel
{
    private const string CustomsId = "56f40101d2720b2a4d8b45d6";
    private const string DefaultPoiSource = MapPoiSources.Extracts;
    private const string DefaultMapBase = NativeMapBaseValues.Cache;
    private const string DefaultMapLayer = NativeMapLayerValues.Surface;
    private const int MaxRenderedPoiCount = 800;
    private const double PoiEdgeTolerance = 0.08;
    private const string NoCoordinateStatus = "未找到有效截图坐标，显示原型点位。";
    private const string NoMapMatchStatus = "最新坐标未匹配到原型地图范围。";

    private static readonly IReadOnlyList<PoiFilterOption> DefaultPoiFilters =
    [
        new("extract", "撤离点", true, 0),
        new("label", "标签", true, 0),
        new("spawnPmc", "PMC 出生", true, 0),
        new("spawnScav", "Scav 出生", true, 0),
        new("spawnBoss", "Boss", true, 0),
        new("spawnSniper", "狙击 Scav", true, 0),
        new("spawnRogue", "Rogue/Bot PMC", true, 0),
        new("locks", "钥匙门", true, 0),
        new("switches", "开关", true, 0),
        new("hazards", "危险区", true, 0),
        new("stationary", "固定武器", true, 0),
        new("btr", "BTR", true, 0),
        new("transits", "转移点", true, 0)
    ];

    private readonly ScreenshotCoordinateService coordinateService;
    private readonly MapMetadataService mapMetadataService;
    private IReadOnlyList<MapPrototypeMetadata> allMaps;
    private string currentPoiSource = DefaultPoiSource;
    private string currentMapBase = DefaultMapBase;
    private string currentMapLayer = DefaultMapLayer;
    private bool showPoi = true;
    private readonly Dictionary<string, bool> poiKindFilters = DefaultPoiFilters.ToDictionary(
        item => item.Kind,
        item => item.IsEnabled,
        StringComparer.OrdinalIgnoreCase);

    public MapPrototypeViewModel()
        : this(new ScreenshotCoordinateService(), new MapMetadataService())
    {
    }

    public MapPrototypeViewModel(
        ScreenshotCoordinateService coordinateService,
        MapMetadataService mapMetadataService)
    {
        this.coordinateService = coordinateService;
        this.mapMetadataService = mapMetadataService;
        allMaps = this.mapMetadataService.LoadMaps();
        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        CurrentMap = AvailableMaps.FirstOrDefault(map => map.Id == CustomsId) ??
            AvailableMaps.FirstOrDefault() ??
            BuildFallbackMap();
    }

    public IReadOnlyList<MapPrototypeMetadata> AllMaps => allMaps;

    public IReadOnlyList<MapPrototypeModel> AvailableMaps { get; private set; }

    public MapPrototypeModel CurrentMap { get; private set; }

    public string CurrentPoiSource => currentPoiSource;

    public string CurrentMapBase => currentMapBase;

    public string CurrentMapLayer => currentMapLayer;

    public IReadOnlyList<MapBaseOption> MapBaseOptions { get; } =
    [
        new(NativeMapBaseValues.Cache, "SVG Cache"),
        new(NativeMapBaseValues.Svg, "Local SVG")
    ];

    public IReadOnlyList<PoiFilterOption> PoiFilters => BuildPoiFilters(CurrentMap);

    public (IReadOnlyList<MapPrototypeModel> Maps, MapPrototypeModel CurrentMap) ReloadAvailableMaps()
    {
        allMaps = mapMetadataService.LoadMaps();
        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        CurrentMap = AvailableMaps.FirstOrDefault(map => map.Id == CurrentMap.Id && map.PoiSource == currentPoiSource) ??
            AvailableMaps.FirstOrDefault(map => map.Id == CustomsId) ??
            AvailableMaps.FirstOrDefault() ??
            BuildFallbackMap();
        currentMapLayer = CurrentMap.MapLayer;

        return (AvailableMaps, CurrentMap);
    }

    public MapPrototypeModel? SelectMap(string? mapId)
    {
        var selected = AvailableMaps.FirstOrDefault(map => map.Id == mapId && map.PoiSource == currentPoiSource) ??
            AvailableMaps.FirstOrDefault(map => map.Id == mapId);
        if (selected is null)
        {
            return null;
        }

        CurrentMap = selected;
        currentMapLayer = CurrentMap.MapLayer;
        return selected;
    }

    public MapPrototypeModel? SelectMapFromLog(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return null;
        }

        return SelectMap(mapId);
    }

    public MapPrototypeModel SelectPoiSource(string? poiSource)
    {
        currentPoiSource = NormalizePoiSource(poiSource);
        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        var selected = allMaps
            .Where(map => map.LocalImagePath is not null)
            .Where(map => map.Id == CurrentMap.Id)
            .Select(map => BuildMapModel(map, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer))
            .FirstOrDefault();

        CurrentMap = selected ?? BuildMapModel(CurrentMap, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        currentMapLayer = CurrentMap.MapLayer;
        return CurrentMap;
    }

    public MapPrototypeModel SelectMapBase(string? mapBase)
    {
        currentMapBase = NormalizeMapBase(mapBase);
        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        var selected = allMaps
            .Where(map => map.LocalImagePath is not null)
            .Where(map => map.Id == CurrentMap.Id)
            .Select(map => BuildMapModel(map, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer))
            .FirstOrDefault();

        CurrentMap = selected ?? BuildMapModel(CurrentMap, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        currentMapLayer = CurrentMap.MapLayer;
        return CurrentMap;
    }

    public MapPrototypeModel SelectMapLayer(string? mapLayer)
    {
        currentMapLayer = NormalizeMapLayer(mapLayer, CurrentMap.Layers);
        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        var selected = allMaps
            .Where(map => map.LocalImagePath is not null)
            .Where(map => map.Id == CurrentMap.Id)
            .Select(map => BuildMapModel(map, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer))
            .FirstOrDefault();

        CurrentMap = selected ?? BuildMapModel(CurrentMap, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        currentMapLayer = CurrentMap.MapLayer;
        return CurrentMap;
    }

    public MapPrototypeModel SelectPoiFilters(IReadOnlyDictionary<string, bool> filters)
    {
        foreach (var option in DefaultPoiFilters)
        {
            if (filters.TryGetValue(option.Kind, out var enabled))
            {
                poiKindFilters[option.Kind] = enabled;
            }
        }

        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        var selected = allMaps
            .Where(map => map.LocalImagePath is not null)
            .Where(map => map.Id == CurrentMap.Id)
            .Select(map => BuildMapModel(map, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer))
            .FirstOrDefault();
        CurrentMap = selected ?? BuildMapModel(CurrentMap, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        currentMapLayer = CurrentMap.MapLayer;
        return CurrentMap;
    }

    public MapProjectionResult RefreshLatestCoordinate(bool autoMapEnabled = false)
    {
        var candidateResult = GetLatestCoordinateCandidates();
        if (!candidateResult.HasCoordinate || candidateResult.Coordinate is null)
        {
            return new MapProjectionResult(false, false, null, candidateResult.StatusMessage);
        }

        if (!ContainsCoordinate(CurrentMap, candidateResult.Coordinate))
        {
            var suggestedMap = GetFirstAvailableCandidate(candidateResult.CandidateMaps);
            if (autoMapEnabled && suggestedMap is not null)
            {
                CurrentMap = suggestedMap;
                return ProjectCoordinateOnCurrentMap(candidateResult.Coordinate, "已自动切换地图并读取最新截图坐标");
            }

            var status = candidateResult.StatusMessage;
            if (suggestedMap is null && candidateResult.CandidateMaps.Count > 0)
            {
                status = $"坐标匹配到 {candidateResult.CandidateMaps[0].Name}，但该地图暂无本地地图资源";
            }

            return new MapProjectionResult(
                true,
                false,
                null,
                status,
                suggestedMap,
                candidateResult.Coordinate);
        }

        return ProjectCoordinateOnCurrentMap(candidateResult.Coordinate, "已读取最新截图坐标");
    }

    public MapPrototypeModel WithPoiVisibility(bool showPoi)
    {
        this.showPoi = showPoi;
        AvailableMaps = BuildAvailableMaps(allMaps, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        var selected = allMaps
            .Where(map => map.LocalImagePath is not null)
            .Where(map => map.Id == CurrentMap.Id)
            .Select(map => BuildMapModel(map, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer))
            .FirstOrDefault();
        CurrentMap = selected ?? BuildMapModel(CurrentMap, currentPoiSource, showPoi, poiKindFilters, currentMapBase, currentMapLayer);
        currentMapLayer = CurrentMap.MapLayer;
        return CurrentMap;
    }

    private MapProjectionResult ProjectCoordinateOnCurrentMap(ScreenshotCoordinate coordinate, string statusPrefix)
    {
        var projected = MapProjection.ProjectWorldToUnit(CurrentMap.Bounds, coordinate.X, coordinate.Z);
        var playerPoint = new MapPrototypePoint(
            $"最新截图 {coordinate.FileName}",
            projected.U,
            projected.V,
            "player",
            coordinate.YawDegrees);

        return new MapProjectionResult(
            true,
            true,
            playerPoint,
            $"{statusPrefix} x {coordinate.X:0.0}, z {coordinate.Z:0.0}",
            Coordinate: coordinate);
    }

    private MapPrototypeModel? GetFirstAvailableCandidate(IReadOnlyList<MapPrototypeMetadata> candidates)
    {
        foreach (var candidate in candidates)
        {
            var available = AvailableMaps.FirstOrDefault(map => map.Id == candidate.Id && map.PoiSource == currentPoiSource) ??
                AvailableMaps.FirstOrDefault(map => map.Id == candidate.Id);
            if (available is not null)
            {
                return available;
            }
        }

        return null;
    }

    private MapCandidateResult GetLatestCoordinateCandidates()
    {
        var coordinate = coordinateService.GetLatestCoordinate();
        if (coordinate is null)
        {
            return new MapCandidateResult(false, [], null, NoCoordinateStatus);
        }

        var candidates = allMaps
            .Where(map => MapProjection.Contains(map.Bounds, coordinate.X, coordinate.Z))
            .OrderBy(map => map.Area)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new MapCandidateResult(true, [], coordinate, NoMapMatchStatus);
        }

        if (candidates.Any(map => map.Id == CurrentMap.Id))
        {
            return new MapCandidateResult(true, candidates, coordinate, "");
        }

        var names = string.Join(" / ", candidates.Take(3).Select(map => map.Name));
        return new MapCandidateResult(true, candidates, coordinate, $"最新坐标可能属于：{names}");
    }

    private static IReadOnlyList<MapPrototypeModel> BuildAvailableMaps(
        IReadOnlyList<MapPrototypeMetadata> maps,
        string poiSource,
        bool showPoi,
        IReadOnlyDictionary<string, bool>? filters = null,
        string mapBase = DefaultMapBase,
        string mapLayer = DefaultMapLayer)
    {
        return maps
            .Where(map => map.LocalImagePath is not null)
            .Select(map => BuildMapModel(map, poiSource, showPoi, filters, mapBase, mapLayer))
            .ToArray();
    }

    private static MapPrototypeModel BuildMapModel(
        MapPrototypeMetadata metadata,
        string poiSource,
        bool showPoi,
        IReadOnlyDictionary<string, bool>? filters = null,
        string mapBase = DefaultMapBase,
        string mapLayer = DefaultMapLayer)
    {
        var aspectRatio = 16.0 / 9.0;
        var width = Math.Abs(metadata.X1 - metadata.X0);
        var height = Math.Abs(metadata.Z1 - metadata.Z0);
        if (width > 0 && height > 0)
        {
            aspectRatio = metadata.ReverseCoordinate ? height / width : width / height;
        }

        return new MapPrototypeModel
        {
            Id = metadata.Id,
            Name = string.IsNullOrWhiteSpace(metadata.Name) ? metadata.Key : metadata.Name,
            Key = metadata.Key,
            NormalizedName = metadata.NormalizedName,
            NameId = metadata.NameId,
            ImagePath = ResolveImagePath(metadata, mapBase),
            MapBase = NormalizeMapBase(mapBase),
            MapLayer = NormalizeMapLayer(mapLayer, metadata.Layers),
            HeightRange = metadata.HeightRange,
            Layers = metadata.Layers,
            MinX = metadata.X0,
            MinZ = metadata.Z0,
            MaxX = metadata.X1,
            MaxZ = metadata.Z1,
            ReverseCoordinate = metadata.ReverseCoordinate,
            AspectRatio = aspectRatio,
            PlayerPoint = new MapPrototypePoint("本机玩家", 0.55, 0.48, "player", 72),
            Points = showPoi ? BuildMapPoints(metadata, poiSource, filters, mapLayer) : [],
            PoiSource = NormalizePoiSource(poiSource),
            PoiKindCounts = CountPoiKinds(metadata, poiSource)
        };
    }

    private static MapPrototypeModel BuildMapModel(
        MapPrototypeModel currentMap,
        string poiSource,
        bool showPoi = true,
        IReadOnlyDictionary<string, bool>? filters = null,
        string mapBase = DefaultMapBase,
        string mapLayer = DefaultMapLayer)
    {
        var normalizedSource = NormalizePoiSource(poiSource);
        var normalizedLayer = NormalizeMapLayer(mapLayer, currentMap.Layers);
        var points = showPoi
            ? currentMap.Points
                .Where(point => IsPoiKindEnabled(point.Kind, filters))
                .Where(point => IsPoiKindVisibleInLayer(point.Kind, normalizedLayer))
                .Take(MaxRenderedPoiCount)
                .ToArray()
            : [];
        return new MapPrototypeModel
        {
            Id = currentMap.Id,
            Name = currentMap.Name,
            Key = currentMap.Key,
            NormalizedName = currentMap.NormalizedName,
            NameId = currentMap.NameId,
            ImagePath = currentMap.ImagePath,
            MapBase = NormalizeMapBase(mapBase),
            MapLayer = normalizedLayer,
            HeightRange = currentMap.HeightRange,
            Layers = currentMap.Layers,
            MinX = currentMap.MinX,
            MinZ = currentMap.MinZ,
            MaxX = currentMap.MaxX,
            MaxZ = currentMap.MaxZ,
            ReverseCoordinate = currentMap.ReverseCoordinate,
            AspectRatio = currentMap.AspectRatio,
            PlayerPoint = currentMap.PlayerPoint,
            Points = points,
            PoiSource = normalizedSource,
            PoiKindCounts = currentMap.PoiKindCounts
        };
    }

    private static IReadOnlyList<MapPrototypePoint> BuildMapPoints(
        MapPrototypeMetadata metadata,
        string poiSource,
        IReadOnlyDictionary<string, bool>? filters = null,
        string mapLayer = DefaultMapLayer)
    {
        var map = new MapPrototypeModel
        {
            MinX = metadata.X0,
            MinZ = metadata.Z0,
            MaxX = metadata.X1,
            MaxZ = metadata.Z1,
            ReverseCoordinate = metadata.ReverseCoordinate
        };

        var normalizedSource = NormalizePoiSource(poiSource);
        var normalizedLayer = NormalizeMapLayer(mapLayer, metadata.Layers);
        var sourcePois = metadata.Pois
            .Where(poi => string.Equals(poi.Source, normalizedSource, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (sourcePois.Length == 0)
        {
            return normalizedSource == MapPoiSources.More ? [] : BuildPrototypePoints(metadata.Key, normalizedSource);
        }

        return sourcePois
            .Where(poi => IsPoiKindEnabled(poi.Kind, filters))
            .Where(poi => IsPoiKindVisibleInLayer(poi.Kind, normalizedLayer))
            .Select(poi => BuildMapPoint(map, poi))
            .OfType<MapPrototypePoint>()
            .Take(MaxRenderedPoiCount)
            .ToArray();
    }

    private static MapPrototypePoint? BuildMapPoint(MapPrototypeModel map, MapPrototypePoi poi)
    {
        var projected = MapProjection.ProjectWorldToUnit(map.Bounds, poi.X, poi.Z);
        if (!TryNormalizePoiUnit(projected, out var u, out var v))
        {
            return null;
        }

        return new MapPrototypePoint(
            poi.Label,
            u,
            v,
            poi.Kind,
            IconName: poi.IconName,
            ShowLabel: poi.ShowLabel,
            LabelText: poi.LabelText);
    }

    private static bool TryNormalizePoiUnit(MapUnitPoint point, out double u, out double v)
    {
        u = 0;
        v = 0;
        if (!double.IsFinite(point.U) ||
            !double.IsFinite(point.V) ||
            point.U < -PoiEdgeTolerance ||
            point.U > 1 + PoiEdgeTolerance ||
            point.V < -PoiEdgeTolerance ||
            point.V > 1 + PoiEdgeTolerance)
        {
            return false;
        }

        u = Math.Clamp(point.U, 0, 1);
        v = Math.Clamp(point.V, 0, 1);
        return true;
    }

    private static IReadOnlyDictionary<string, int> CountPoiKinds(MapPrototypeMetadata metadata, string poiSource)
    {
        var normalizedSource = NormalizePoiSource(poiSource);
        return metadata.Pois
            .Where(poi => string.Equals(poi.Source, normalizedSource, StringComparison.OrdinalIgnoreCase))
            .GroupBy(poi => poi.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPoiKindEnabled(string kind, IReadOnlyDictionary<string, bool>? filters)
    {
        return filters is null || !filters.TryGetValue(kind, out var enabled) || enabled;
    }

    private static bool IsPoiKindVisibleInLayer(string kind, string mapLayer)
    {
        return mapLayer switch
        {
            NativeMapLayerValues.Main => false,
            NativeMapLayerValues.Extracts => string.Equals(kind, "extract", StringComparison.OrdinalIgnoreCase),
            NativeMapLayerValues.Spawns => IsSpawnPoiKind(kind),
            NativeMapLayerValues.Objects => IsObjectPoiKind(kind),
            _ => true
        };
    }

    private static bool IsSpawnPoiKind(string kind)
    {
        return string.Equals(kind, "spawnPmc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "spawnScav", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "spawnBoss", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "spawnSniper", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "spawnRogue", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObjectPoiKind(string kind)
    {
        return string.Equals(kind, "locks", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "switches", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "hazards", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "stationary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "btr", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "transits", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveImagePath(MapPrototypeMetadata metadata, string mapBase)
    {
        if (NormalizeMapBase(mapBase) == NativeMapBaseValues.Svg &&
            !string.IsNullOrWhiteSpace(metadata.SvgImagePath) &&
            File.Exists(metadata.SvgImagePath))
        {
            return metadata.SvgImagePath;
        }

        return metadata.LocalImagePath ?? metadata.SvgImagePath ?? "";
    }

    private IReadOnlyList<PoiFilterOption> BuildPoiFilters(MapPrototypeModel map)
    {
        return DefaultPoiFilters
            .Select(option =>
            {
                var enabled = !poiKindFilters.TryGetValue(option.Kind, out var value) || value;
                var count = map.PoiKindCounts.TryGetValue(option.Kind, out var itemCount) ? itemCount : 0;
                return option with { IsEnabled = enabled, Count = count };
            })
            .ToArray();
    }

    private static IReadOnlyList<MapPrototypePoint> BuildPrototypePoints(string key, string poiSource = DefaultPoiSource)
    {
        if (NormalizePoiSource(poiSource) == MapPoiSources.More)
        {
            return [];
        }

        if (NormalizePoiSource(poiSource) == MapMetadataService.LabelsPoiSource)
        {
            return
            [
                new("Labels 无可用点位", 0.5, 0.5, "label")
            ];
        }

        if (string.Equals(key, "customs", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("宿舍", 0.34, 0.42, "poi"),
                new("旧加油站", 0.69, 0.63, "extract"),
                new("大红仓", 0.12, 0.54, "poi"),
                new("新加油站", 0.55, 0.57, "poi"),
                new("十字路口", 0.08, 0.42, "extract")
            ];
        }

        return
        [
            new("预览点位", 0.35, 0.35, "poi"),
            new("预览撤离点", 0.72, 0.58, "extract"),
            new("参考点", 0.5, 0.5, "poi")
        ];
    }

    private static MapPrototypeModel BuildFallbackMap()
    {
        return new MapPrototypeModel
        {
            Id = CustomsId,
            Name = "海关预览",
            Key = "customs",
            ImagePath = "",
            MapBase = DefaultMapBase,
            MapLayer = DefaultMapLayer,
            MinX = 698,
            MinZ = -307,
            MaxX = -372,
            MaxZ = 237,
            AspectRatio = 16.0 / 9.0,
            PlayerPoint = new MapPrototypePoint("本机玩家", 0.55, 0.48, "player", 72),
            Points = BuildPrototypePoints("customs"),
            PoiSource = DefaultPoiSource
        };
    }

    private static string NormalizePoiSource(string? value)
    {
        return MapPoiSources.Normalize(value);
    }

    private static string NormalizeMapBase(string? value)
    {
        return string.Equals(value, NativeMapBaseValues.Svg, StringComparison.OrdinalIgnoreCase)
            ? NativeMapBaseValues.Svg
            : NativeMapBaseValues.Cache;
    }

    private static string NormalizeMapLayer(string? value, IReadOnlyList<MapLayerMetadata>? layers = null)
    {
        var text = value?.Trim() ?? "";
        var normalized = text.ToLowerInvariant();
        if (normalized is NativeMapLayerValues.Auto or "auto")
        {
            return NativeMapLayerValues.Surface;
        }

        if (normalized is NativeMapLayerValues.Surface)
        {
            return normalized;
        }

        if (normalized is NativeMapLayerValues.Main or NativeMapLayerValues.Extracts or NativeMapLayerValues.Spawns or NativeMapLayerValues.Objects)
        {
            return normalized;
        }

        return layers?.FirstOrDefault(layer => string.Equals(layer.Name, text, StringComparison.OrdinalIgnoreCase))?.Name ??
            NativeMapLayerValues.Surface;
    }

    private static bool ContainsCoordinate(MapPrototypeModel map, ScreenshotCoordinate coordinate)
    {
        return MapProjection.Contains(map.Bounds, coordinate.X, coordinate.Z);
    }
}
