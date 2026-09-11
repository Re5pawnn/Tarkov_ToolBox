using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovMapLocatorDesktop.Models;
using TarkovMapLocatorDesktop.Services;

namespace TarkovMapLocatorDesktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan CoordinateStaleAfter = TimeSpan.FromMinutes(3);
    private const int FullMapDecodePixelWidthLimit = 4096;
    private const int MapImageCacheCapacity = 2;
    private readonly LinkedList<MapDefinition> _mapImageLru = [];
    private readonly HashSet<MapDefinition> _mapImagesLoading = [];
    private MapDefinition? _selectedMap;
    private LiveCoordinate? _liveCoordinate;
    private string? _liveCoordinateMapId;
    private MapMarker? _playerMarker;
    private string? _playerMarkerProjectionError;
    private bool _isCoordinateStale;
    private string _assetStatus = "正在检查本地地图资产…";
    private string _lastUpdate = "刚刚";

    public ObservableCollection<MapDefinition> Maps { get; } = [];

    public MapDefinition? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (ReferenceEquals(_selectedMap, value)) return;
            _selectedMap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentMapName));
            OnPropertyChanged(nameof(CurrentMapSubtitle));
            OnPropertyChanged(nameof(CurrentMapPlayers));
            OnPropertyChanged(nameof(CurrentMapExtracts));
            OnPropertyChanged(nameof(CurrentMapDuration));
            OnPropertyChanged(nameof(CurrentMapRaidStatsNote));
            OnPropertyChanged(nameof(CurrentMapCoordinate));
            OnPropertyChanged(nameof(CurrentCoordinateStatus));
            OnPropertyChanged(nameof(CurrentMapImage));
            OnPropertyChanged(nameof(CurrentMapAvailable));
            RefreshPlayerMarker();
            LastUpdate = DateTime.Now.ToString("HH:mm");
            _ = EnsureMapImageAsync(value);
        }
    }

    public string CurrentMapName => SelectedMap?.Name ?? "未选择地图";
    public string CurrentMapSubtitle => SelectedMap?.Subtitle ?? "本地地图模式";
    public string CurrentMapPlayers => SelectedMap?.Players ?? "—";
    public string CurrentMapExtracts => SelectedMap?.Extracts ?? "—";
    public string CurrentMapDuration => SelectedMap?.Duration ?? "—";
    public string CurrentMapRaidStatsNote => SelectedMap?.RaidStatsNote ?? "暂无地图资料";
    public string CurrentMapCoordinate => ActiveCoordinateForSelectedMap is { } coordinate
        ? $"X {coordinate.X:0.0}   Z {coordinate.Z:0.0}"
        : "等待本机截图坐标";
    public string CurrentCoordinateStatus => ActiveCoordinateForSelectedMap is not { } coordinate
        ? "等待新的含坐标截图"
        : _playerMarkerProjectionError is not null
            ? "已读取坐标，但无法投影到当前地图"
        : _isCoordinateStale
            ? $"截图坐标已过期 · {coordinate.CapturedAt.LocalDateTime:HH:mm}"
            : $"方向 {coordinate.YawDegrees:0}° · 截图 {coordinate.CapturedAt.LocalDateTime:HH:mm}";
    public ImageSource? CurrentMapImage => SelectedMap?.ImageSource;
    public bool CurrentMapAvailable => CurrentMapImage is not null;
    public MapMarker? PlayerMarker => _playerMarker;
    public string? PlayerMarkerProjectionError => _playerMarkerProjectionError;
    public LiveCoordinate? LiveCoordinate => _liveCoordinate;
    public bool IsCoordinateStale => _isCoordinateStale;
    private LiveCoordinate? ActiveCoordinateForSelectedMap =>
        _liveCoordinate is { } coordinate &&
        string.Equals(_liveCoordinateMapId, SelectedMap?.Id, StringComparison.OrdinalIgnoreCase)
            ? coordinate
            : null;

    public string AssetStatus
    {
        get => _assetStatus;
        private set { _assetStatus = value; OnPropertyChanged(); }
    }

    public string LastUpdate
    {
        get => _lastUpdate;
        private set { _lastUpdate = value; OnPropertyChanged(); }
    }

    public MainViewModel()
    {
        LoadMaps();
    }

    public bool SelectMapFromLog(string? mapKey)
    {
        if (string.IsNullOrWhiteSpace(mapKey)) return false;
        var target = Maps.FirstOrDefault(map => string.Equals(map.Id, mapKey.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null || ReferenceEquals(target, SelectedMap)) return false;

        SelectedMap = target;
        return true;
    }

    public bool SetLiveCoordinate(LiveCoordinate coordinate, string mapId)
    {
        mapId = mapId.Trim();
        if (_liveCoordinate == coordinate && string.Equals(_liveCoordinateMapId, mapId, StringComparison.OrdinalIgnoreCase)) return false;
        _liveCoordinate = coordinate;
        _liveCoordinateMapId = mapId;
        _isCoordinateStale = IsStale(coordinate, DateTimeOffset.Now);
        RefreshPlayerMarker();
        OnPropertyChanged(nameof(LiveCoordinate));
        OnPropertyChanged(nameof(CurrentMapCoordinate));
        OnPropertyChanged(nameof(CurrentCoordinateStatus));
        OnPropertyChanged(nameof(IsCoordinateStale));
        LastUpdate = coordinate.CapturedAt.LocalDateTime.ToString("HH:mm");
        return true;
    }

    public LiveCoordinate? GetShareableCoordinate(DateTimeOffset now) =>
        ActiveCoordinateForSelectedMap is { } coordinate && !IsStale(coordinate, now)
            ? coordinate
            : null;

    public bool RefreshCoordinateFreshness(DateTimeOffset now)
    {
        var stale = _liveCoordinate is { } coordinate && IsStale(coordinate, now);
        if (_isCoordinateStale == stale) return false;

        _isCoordinateStale = stale;
        RefreshPlayerMarker();
        OnPropertyChanged(nameof(CurrentCoordinateStatus));
        OnPropertyChanged(nameof(IsCoordinateStale));
        return true;
    }

    public bool MarkLiveCoordinateStale()
    {
        if (_liveCoordinate is null || _isCoordinateStale) return false;

        _isCoordinateStale = true;
        RefreshPlayerMarker();
        OnPropertyChanged(nameof(CurrentCoordinateStatus));
        OnPropertyChanged(nameof(IsCoordinateStale));
        return true;
    }

    private void RefreshPlayerMarker()
    {
        _playerMarker = null;
        _playerMarkerProjectionError = null;
        if (ActiveCoordinateForSelectedMap is not { } coordinate)
        {
            OnPropertyChanged(nameof(PlayerMarker));
            OnPropertyChanged(nameof(PlayerMarkerProjectionError));
            return;
        }

        if (SelectedMap?.WorldBounds is not { } bounds)
        {
            _playerMarkerProjectionError = $"地图 {SelectedMap?.Id ?? "unknown"} 未配置世界坐标边界";
        }
        else if (!bounds.TryProject(coordinate.X, coordinate.Z, out var x, out var y))
        {
            _playerMarkerProjectionError =
                $"坐标 X {coordinate.X:0.###} / Z {coordinate.Z:0.###} 超出地图 {SelectedMap.Id} 的投影边界 {bounds}";
        }
        else
        {
            _playerMarker = new MapMarker
            {
                Type = _isCoordinateStale ? "player-stale" : "player",
                Label = _isCoordinateStale ? "最后位置（已过期）" : "当前位置",
                X = x,
                Y = y,
                ShowLabel = true,
                HeadingDegrees = bounds.ProjectHeading(coordinate.YawDegrees),
                WorldX = coordinate.X,
                WorldHeight = coordinate.Y,
                WorldZ = coordinate.Z
            };
        }

        OnPropertyChanged(nameof(PlayerMarker));
        OnPropertyChanged(nameof(PlayerMarkerProjectionError));
        OnPropertyChanged(nameof(CurrentCoordinateStatus));
    }

    private static bool IsStale(LiveCoordinate coordinate, DateTimeOffset now) =>
        !LocalRaidMonitorService.IsPlausibleCoordinateTime(coordinate.CapturedAt, now) ||
        now - coordinate.CapturedAt > CoordinateStaleAfter;

    private void LoadMaps()
    {
        var assetDirectory = FindAssetDirectory();
        var pointLoad = LocalMapPointService.Load();
        var mapCatalog = MapCatalogService.Load();
        var definitions = mapCatalog.Maps;

        foreach (var definition in definitions)
        {
            var imagePath = assetDirectory is null ? null : Path.Combine(assetDirectory, definition.ImageFileName);
            if (imagePath is not null && !File.Exists(imagePath)) imagePath = null;
            var thumbnail = imagePath is null ? null : LoadImage(imagePath, 112);
            pointLoad.MarkersByMap.TryGetValue(definition.Id, out var markers);
            var worldBounds = pointLoad.BoundsByMap.TryGetValue(definition.Id, out var bounds)
                ? bounds
                : (MapCoordinateBounds?)null;
            definition.ImageFilePath = imagePath;
            definition.ThumbnailSource = thumbnail;
            definition.Markers = markers ?? [];
            definition.WorldBounds = worldBounds;
            Maps.Add(definition);
        }

        var loadedCount = Maps.Count(map => map.ImageFilePath is not null);
        AssetStatus = assetDirectory is null
            ? "未找到 assets/maps/native-cache；请将程序放回项目根目录运行"
            : pointLoad.IsAvailable
                ? $"已索引 {loadedCount} / {Maps.Count} 张地图 · 同步 {pointLoad.TotalMarkerCount} 个点位"
                : $"已索引 {loadedCount} / {Maps.Count} 张本地地图资产 · 点位数据不可用";
        SelectedMap = Maps.FirstOrDefault(map => map.Id == "customs") ?? Maps.FirstOrDefault();
    }

    private async Task EnsureMapImageAsync(MapDefinition? map)
    {
        if (map is null || string.IsNullOrWhiteSpace(map.ImageFilePath)) return;

        if (map.ImageSource is not null)
        {
            TouchMapImage(map);
            TrimMapImageCache();
            return;
        }

        if (!_mapImagesLoading.Add(map)) return;
        var watch = Stopwatch.StartNew();
        try
        {
            var image = await Task.Run(() => LoadImage(map.ImageFilePath, FullMapDecodePixelWidthLimit));
            if (image is null) return;

            map.ImageSource = image;
            TouchMapImage(map);
            TrimMapImageCache();
            watch.Stop();
            if (image is BitmapSource source)
            {
                RuntimeLogService.Info(
                    "地图资源",
                    "地图大图后台加载完成",
                    $"地图: {map.Name} ({map.Id})\n" +
                    $"解码尺寸: {source.PixelWidth:N0} × {source.PixelHeight:N0}\n" +
                    $"仅在原图超过 {FullMapDecodePixelWidthLimit:N0} px 时缩小，不再放大小图\n" +
                    $"耗时: {watch.Elapsed.TotalMilliseconds:N0} ms\n" +
                    $"文件: {map.ImageFilePath}");
            }

            if (ReferenceEquals(map, SelectedMap))
            {
                OnPropertyChanged(nameof(CurrentMapImage));
                OnPropertyChanged(nameof(CurrentMapAvailable));
                MapImageLoaded?.Invoke(map);
            }
        }
        finally
        {
            _mapImagesLoading.Remove(map);
        }
    }

    private void TouchMapImage(MapDefinition map)
    {
        var node = _mapImageLru.First;
        while (node is not null)
        {
            var next = node.Next;
            if (ReferenceEquals(node.Value, map))
            {
                _mapImageLru.Remove(node);
                break;
            }
            node = next;
        }

        _mapImageLru.AddFirst(map);
    }

    private void TrimMapImageCache()
    {
        while (_mapImageLru.Count > MapImageCacheCapacity)
        {
            var node = _mapImageLru.Last;
            if (node is null) return;

            _mapImageLru.RemoveLast();
            if (ReferenceEquals(node.Value, _selectedMap))
            {
                _mapImageLru.AddFirst(node.Value);
                continue;
            }

            node.Value.ImageSource = null;
            RuntimeLogService.Trace(
                "地图资源",
                "释放地图大图缓存",
                $"地图: {node.Value.Name} ({node.Value.Id})\n缓存上限: {MapImageCacheCapacity} 张");
        }
    }

    private static ImageSource? LoadImage(string filePath, int? decodePixelWidthLimit = null)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidthLimit is { } limit && GetPixelWidth(filePath) > limit)
                image.DecodePixelWidth = limit;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error(
                "地图资源",
                "图片加载失败",
                exception,
                $"文件: {filePath}\n解码宽度上限: {decodePixelWidthLimit?.ToString() ?? "原始尺寸"}");
            return null;
        }
    }

    private static int GetPixelWidth(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return decoder.Frames.Count > 0 ? decoder.Frames[0].PixelWidth : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? FindAssetDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "assets", "maps", "native-cache");
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<MapDefinition>? MapImageLoaded;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
