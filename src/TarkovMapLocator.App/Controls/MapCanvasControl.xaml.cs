using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using System.Text.Json.Nodes;
using Windows.Foundation;
using TarkovMapLocator.App.Models;
using TarkovMapLocator.Core.Logs;
using TarkovMapLocator.Core.Maps;

namespace TarkovMapLocator.App.Controls;

public sealed partial class MapCanvasControl : UserControl
{
    private const string WebMapIconBaseUrl = "https://cdn.kaedeori.com/uploads/tarkov/map-icons";
    private const double WebMapViewportAspectRatio = 16.0 / 9.0;
    private const double MinimumViewportWidth = 320;
    private const double MinimumViewportHeight = MinimumViewportWidth / WebMapViewportAspectRatio;
    private const double MarkerYawVisualOffsetDegrees = 180;

    private MapPrototypeModel? map;
    private readonly List<FrameworkElement> overlayElements = [];
    private readonly List<FrameworkElement> peerElements = [];
    private readonly Dictionary<string, FrameworkElement> poiElementsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> peerElementsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> imageSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> markerIconSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MapSelectorOption> mapSelectorOptions = [];
    private FrameworkElement? playerMarker;
    private double mapWidth;
    private double mapHeight;
    private double mapOffsetX;
    private double mapOffsetY;
    private double fitScale = 1;
    private double scale = 1;
    private double translateX;
    private double translateY;
    private bool isFittingViewport;
    private bool isDragging;
    private uint? dragPointerId;
    private Point lastPointerPosition;
    private bool suppressMapSelectionChanged;
    private bool suppressMapBaseSelectionChanged;
    private bool suppressPoiSourceSelectionChanged;
    private bool suppressLanSyncConfigChanged;
    private bool suppressScreenFilterConfigChanged;
    private bool autoRefreshEnabled;
    private bool autoMapEnabled;
    private bool pipEnabled;
    private bool showPoi = true;
    private string? pinnedStatusMessage;
    private string startupPageLabel = "启动: Native";
    private string screenshotDirectoryLabel = "未选择";
    private string logDirectoryLabel = "未选择";
    private string watchStatusLabel = "未监听";
    private string latestCoordinateLabel = "暂无截图坐标";
    private string logStatusLabel = "日志: 未选择";
    private string raidStateLabel = "战局: 未知";
    private string logMapLabel = "";
    private string latestLogLabel = "最近日志: --";
    private string latestLogEventLabel = "";
    private RaidLogSnapshot latestRaidSnapshot = RaidLogSnapshot.NoDirectory();
    private string syncMode = "host";
    private bool raidDetailsExpanded;
    private readonly IReadOnlyList<PoiSourceOption> poiSourceOptions =
    [
        new("extracts", "Extracts"),
        new("labels", "Labels"),
        new("more", "More")
    ];
    private readonly IReadOnlyList<ScreenFilterPresetOption> screenFilterPresetOptions =
    [
        new("default", "默认", new ScreenFilterPreset(1.0, 0, 0, 128, 128, 128)),
        new("day-clear", "白天晴天", new ScreenFilterPreset(1.3, 6, 4, 128, 128, 128)),
        new("day-cloudy", "白天阴天", new ScreenFilterPreset(1.55, 55, 21, 128, 128, 128)),
        new("night", "极致夜晚", new ScreenFilterPreset(2.9, 100, 37, 128, 128, 128)),
        new("custom", "自定义", ScreenFilterPreset.Default)
    ];
    private readonly List<ScreenFilterDisplayOption> screenFilterDisplayOptions = [];
    private readonly Dictionary<string, bool> poiFilterState = new(StringComparer.OrdinalIgnoreCase);

    public MapCanvasControl()
    {
        InitializeComponent();
        SyncColorComboBox.SelectedIndex = 0;
        PoiSourceSelector.ItemsSource = poiSourceOptions;
        PoiSourceSelector.DisplayMemberPath = nameof(PoiSourceOption.Label);
        PoiSourceSelector.SelectedValuePath = nameof(PoiSourceOption.Value);
        PoiSourceSelector.SelectedItem = poiSourceOptions[0];
        ScreenFilterPresetComboBox.ItemsSource = screenFilterPresetOptions;
        SetScreenFilterDisplays([]);
        ApplyScreenFilterConfig(null);
    }

    public event EventHandler? RefreshPositionRequested;

    public event EventHandler? PickScreenshotDirectoryRequested;

    public event EventHandler? PickLogDirectoryRequested;

    public event EventHandler? ToggleWatchRequested;

    public event EventHandler<bool>? AutoRefreshChanged;

    public event EventHandler<bool>? AutoMapChanged;

    public event EventHandler<bool>? PipChanged;

    public event EventHandler<bool>? ShowPoiChanged;

    public event EventHandler<bool>? StartupPageChanged;

    public event EventHandler? CheckMapCacheRequested;

    public event EventHandler<string>? MapSelectionRequested;

    public event EventHandler<string>? MapBaseSelectionRequested;

    public event EventHandler<string>? PoiSourceSelectionRequested;

    public event EventHandler<IReadOnlyDictionary<string, bool>>? PoiFiltersChanged;

    public event EventHandler<bool>? RaidDetailsChanged;

    public event EventHandler<NativeLanSyncRequest>? LanSyncConfigChanged;

    public event EventHandler<NativeLanSyncRequest>? LanSyncStartRequested;

    public event EventHandler? LanSyncStopRequested;

    public event EventHandler<ScreenFilterRequest>? ScreenFilterConfigChanged;

    public event EventHandler<ScreenFilterRequest>? ScreenFilterApplyRequested;

    public event EventHandler<ScreenFilterRequest>? ScreenFilterResetRequested;

    public double PollingIntervalSeconds
    {
        get
        {
            var value = PollingIntervalBox.Value;
            if (!double.IsFinite(value))
            {
                return 2;
            }

            return Math.Clamp(value, 1, 30);
        }
    }

    public bool IsWatchEnabled { get; private set; }

    public bool IsAutoRefreshEnabled => autoRefreshEnabled;

    public NativeLanSyncRequest CurrentLanSyncRequest => new(
        string.IsNullOrWhiteSpace(SyncNameTextBox.Text) ? "本机玩家" : SyncNameTextBox.Text.Trim(),
        GetSelectedSyncColor(),
        syncMode,
        SyncRemoteEndpointTextBox.Text?.Trim() ?? "");

    public ScreenFilterRequest CurrentScreenFilterRequest => new(
        ScreenFilterPresetComboBox.SelectedItem is ScreenFilterPresetOption preset ? preset.Id : "custom",
        ScreenFilterDisplayComboBox.SelectedItem is ScreenFilterDisplayOption display ? display.DisplayName : "",
        ReadScreenFilterNumber(ScreenFilterGammaBox, 0.2, 5.0, 1.0),
        (int)Math.Round(ReadScreenFilterNumber(ScreenFilterBrightnessBox, -100, 100, 0)),
        (int)Math.Round(ReadScreenFilterNumber(ScreenFilterContrastBox, -100, 100, 0)),
        (int)Math.Round(ReadScreenFilterNumber(ScreenFilterRedBox, 0, 255, 128)),
        (int)Math.Round(ReadScreenFilterNumber(ScreenFilterGreenBox, 0, 255, 128)),
        (int)Math.Round(ReadScreenFilterNumber(ScreenFilterBlueBox, 0, 255, 128)));

    public void SetAvailableMaps(IReadOnlyList<MapPrototypeModel> maps, string selectedMapId)
    {
        suppressMapSelectionChanged = true;
        mapSelectorOptions.Clear();
        mapSelectorOptions.Add(MapSelectorOption.Auto);
        mapSelectorOptions.AddRange(maps.Select(MapSelectorOption.FromMap));
        MapSelector.ItemsSource = mapSelectorOptions;
        SelectCurrentMapOption(selectedMapId);
        suppressMapSelectionChanged = false;
    }

    private void SelectCurrentMapOption(string selectedMapId)
    {
        MapSelector.SelectedItem = autoMapEnabled
            ? mapSelectorOptions.FirstOrDefault(item => item.IsAuto)
            : mapSelectorOptions.FirstOrDefault(item => item.MapId == selectedMapId);
    }

    public void SetMapBaseOptions(IReadOnlyList<MapBaseOption> options, string selectedValue)
    {
        suppressMapBaseSelectionChanged = true;
        MapBaseSelector.ItemsSource = options;
        MapBaseSelector.DisplayMemberPath = nameof(MapBaseOption.Label);
        MapBaseSelector.SelectedValuePath = nameof(MapBaseOption.Value);
        MapBaseSelector.SelectedItem = options.FirstOrDefault(item => item.Value == selectedValue) ??
            options.FirstOrDefault();
        suppressMapBaseSelectionChanged = false;
    }

    public void LoadMap(MapPrototypeModel nextMap)
    {
        map = nextMap;
        pinnedStatusMessage = null;
        UpdateStatusTexts("适配窗口");

        if (!string.IsNullOrWhiteSpace(nextMap.ImagePath) && File.Exists(nextMap.ImagePath))
        {
            MapImage.Source = CreateImageSource(nextMap.ImagePath);
            EmptyStateText.Visibility = Visibility.Collapsed;
        }
        else
        {
            MapImage.Source = null;
            EmptyStateText.Visibility = Visibility.Visible;
            EmptyStateText.Text = "No usable map image.";
        }

        suppressMapSelectionChanged = true;
        SelectCurrentMapOption(nextMap.Id);
        suppressMapSelectionChanged = false;

        if (MapBaseSelector.ItemsSource is IEnumerable<MapBaseOption> mapBases)
        {
            suppressMapBaseSelectionChanged = true;
            MapBaseSelector.SelectedItem = mapBases.FirstOrDefault(item => item.Value == nextMap.MapBase);
            suppressMapBaseSelectionChanged = false;
        }

        suppressPoiSourceSelectionChanged = true;
        PoiSourceSelector.SelectedItem = poiSourceOptions.FirstOrDefault(item => item.Value == nextMap.PoiSource) ??
            poiSourceOptions[0];
        suppressPoiSourceSelectionChanged = false;
        SetPoiFilters(nextMap.PoiKindCounts.Select(item =>
        {
            var enabled = !poiFilterState.TryGetValue(item.Key, out var value) || value;
            return new PoiFilterOption(item.Key, GetPoiKindLabel(item.Key), enabled, item.Value);
        }).ToArray());

        RenderOverlays();
        FitToViewport(resetView: true);
        UpdateRaidInfoText();
    }

    public void ApplyProjectionResult(MapProjectionResult result)
    {
        if (result.SuggestedMap is not null)
        {
            LoadMap(result.SuggestedMap);
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            MapStatusText.Text = result.StatusMessage;
        }

        if (result.PlayerPoint is not null)
        {
            UpdatePlayerMarker(result.PlayerPoint);
        }

        UpdateRaidSnapshot(result, IsWatchEnabled);
    }

    public void SetPipEnabled(bool enabled)
    {
        pipEnabled = enabled;
        PipButton.Content = pipEnabled ? "画中画: On" : "画中画: Off";
    }

    public void ApplyRaidLogSnapshot(RaidLogSnapshot snapshot)
    {
        latestRaidSnapshot = snapshot;
        logStatusLabel = snapshot.LogStatus switch
        {
            RaidLogStatus.NoDirectory => "日志: 未选择",
            RaidLogStatus.Waiting => "日志: 等待日志",
            RaidLogStatus.Ready => "日志: 已读取",
            RaidLogStatus.Error => "日志: 解析失败",
            _ => "日志: 未知"
        };
        raidStateLabel = $"战局: {FormatRaidState(snapshot.RaidState)}";
        logMapLabel = string.IsNullOrWhiteSpace(snapshot.MapName) ? "" : $"Log地图: {snapshot.MapName}";
        latestLogLabel = snapshot.LastScanAt is null ? "最近日志: --" : $"最近日志: {snapshot.LastScanAt.Value.LocalDateTime:HH:mm:ss}";
        latestLogEventLabel = string.IsNullOrWhiteSpace(snapshot.LastEventTitle)
            ? ""
            : $"{snapshot.LastEventTitle}: {snapshot.LastEventDetail ?? "Recorded"}";

        if (snapshot.LogStatus == RaidLogStatus.Error && !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            latestLogEventLabel = snapshot.ErrorMessage;
        }

        UpdateRaidSummary(snapshot);
        UpdateRaidInfoText();
    }

    public void ShowStatus(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            pinnedStatusMessage = message;
            MapStatusText.Text = message;
        }
    }

    public bool IsAutoMapEnabled => autoMapEnabled;

    public void SetAutoRefreshEnabled(bool enabled)
    {
        autoRefreshEnabled = enabled;
        AutoRefreshButton.Content = enabled ? "自动刷新: On" : "自动刷新: Off";
        pinnedStatusMessage = null;
        ApplyTransform();
    }

    public void SetAutoMapEnabled(bool enabled)
    {
        autoMapEnabled = enabled;
        AutoMapButton.Content = enabled ? "自动地图: On" : "自动地图: Off";
        pinnedStatusMessage = null;
        suppressMapSelectionChanged = true;
        SelectCurrentMapOption(map?.Id ?? "");
        suppressMapSelectionChanged = false;
        ApplyTransform();
    }

    public void SetShowPoiEnabled(bool enabled)
    {
        showPoi = enabled;
        ShowPoiButton.IsChecked = enabled;
        pinnedStatusMessage = null;
        ApplyTransform();
    }

    public void SetRaidDetailsExpanded(bool expanded)
    {
        raidDetailsExpanded = expanded;
        RaidDetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RaidDetailsButton.Content = expanded ? "收起详情" : "展开详情";
    }

    public void SetStartupPagePreference(bool nativeMap)
    {
        startupPageLabel = "启动: Native";
        UpdateStatusTexts();
    }

    public void SetLocalPathLabels(IReadOnlyDictionary<string, string> paths)
    {
        screenshotDirectoryLabel = FormatPathLabel(paths.TryGetValue("screenshot", out var screenshotPath) ? screenshotPath : "");
        logDirectoryLabel = FormatPathLabel(paths.TryGetValue("game", out var gamePath) ? gamePath : "");
        ScreenshotDirectoryText.Text = $"截图目录: {screenshotDirectoryLabel}";
        LogDirectoryText.Text = $"Log目录: {logDirectoryLabel}";
        UpdateRaidInfoText();
    }

    public void SetWatchEnabled(bool enabled)
    {
        IsWatchEnabled = enabled;
        WatchButton.Content = enabled ? "停止监听" : "开始监听";
        watchStatusLabel = enabled ? "等待截图" : "未监听";
        UpdateRaidInfoText();
        UpdateStatusTexts(enabled ? "Watching" : "Ready");
    }

    public void ApplyLanSyncConfig(JsonObject? config)
    {
        config ??= [];
        var displayName = ReadString(config, "displayName", "本机玩家");
        var color = ReadString(config, "color", "#4fd1ff");
        var mode = ReadString(config, "mode", "off");
        var configuredSyncMode = ReadString(config, "syncMode", mode);
        var endpoint = ReadString(config, "remoteEndpoint", "");

        suppressLanSyncConfigChanged = true;
        try
        {
            syncMode = mode == "join" || configuredSyncMode == "join" ? "join" : "host";
            SyncRemoteEndpointTextBox.IsEnabled = syncMode == "join";
            SyncNameTextBox.Text = displayName;
            SelectSyncColor(color);
            SyncRemoteEndpointTextBox.Text = endpoint;
            SyncStatusText.Text = $"同步状态: 未开启 | 名称 {displayName}";
        }
        finally
        {
            suppressLanSyncConfigChanged = false;
        }
    }

    public void SetScreenFilterDisplays(IReadOnlyList<ScreenDisplayInfo> displays)
    {
        var selectedDisplayName = ScreenFilterDisplayComboBox.SelectedItem is ScreenFilterDisplayOption selected
            ? selected.DisplayName
            : "";

        screenFilterDisplayOptions.Clear();
        screenFilterDisplayOptions.Add(new ScreenFilterDisplayOption("", "全部屏幕"));
        screenFilterDisplayOptions.AddRange(displays.Select(display => new ScreenFilterDisplayOption(display.DeviceName, display.Label)));

        suppressScreenFilterConfigChanged = true;
        try
        {
            ScreenFilterDisplayComboBox.ItemsSource = null;
            ScreenFilterDisplayComboBox.ItemsSource = screenFilterDisplayOptions;
            SelectScreenFilterDisplay(selectedDisplayName);
        }
        finally
        {
            suppressScreenFilterConfigChanged = false;
        }
    }

    public void ApplyScreenFilterConfig(JsonObject? config)
    {
        config ??= [];

        suppressScreenFilterConfigChanged = true;
        try
        {
            SelectScreenFilterPreset(ReadString(config, "presetId", "default"));
            SelectScreenFilterDisplay(ReadString(config, "displayName", ""));
            SetScreenFilterValues(new ScreenFilterPreset(
                ReadDouble(config, "gamma", 0.2, 5.0, 1.0),
                ReadInt(config, "brightness", -100, 100, 0),
                ReadInt(config, "contrast", -100, 100, 0),
                ReadInt(config, "red", 0, 255, 128),
                ReadInt(config, "green", 0, 255, 128),
                ReadInt(config, "blue", 0, 255, 128)));
            ScreenFilterStatusText.Text = "屏幕滤镜: 参数已加载";
        }
        finally
        {
            suppressScreenFilterConfigChanged = false;
        }
    }

    public void ShowScreenFilterStatus(string status)
    {
        ScreenFilterStatusText.Text = string.IsNullOrWhiteSpace(status) ? "屏幕滤镜: --" : status;
    }

    public void ApplyLanSyncState(NativeLanSyncState state)
    {
        var isHostRunning = state.IsRunning && string.Equals(state.Mode, "host", StringComparison.OrdinalIgnoreCase);
        var isJoinRunning = state.IsRunning && string.Equals(state.Mode, "join", StringComparison.OrdinalIgnoreCase);
        SyncHostButton.IsEnabled = !state.IsRunning;
        SyncJoinButton.IsEnabled = !state.IsRunning;
        SyncStopButton.IsEnabled = isHostRunning;
        SyncJoinDisconnectButton.IsEnabled = isJoinRunning;
        SyncJoinDisconnectButton.Visibility = isJoinRunning ? Visibility.Visible : Visibility.Collapsed;
        SyncRemoteEndpointTextBox.IsEnabled = !state.IsRunning;
        SyncStatusText.Text = state.Status;
        SyncLocalEndpointText.Text = FormatLocalSyncPortText(state.LocalEndpoint);
        SyncHintText.Text = state.ConnectionHint;
        SyncErrorText.Text = string.IsNullOrWhiteSpace(state.LastError) ? "" : $"最近错误：{state.LastError}";
        SyncErrorText.Visibility = string.IsNullOrWhiteSpace(state.LastError) ? Visibility.Collapsed : Visibility.Visible;
        SyncPeerRepeater.ItemsSource = state.Peers.Count > 0
            ? state.Peers.Select(peer => new SyncPeerItem(
                peer.DisplayName,
                peer.RoleText,
                peer.Color,
                peer.MapName,
                peer.LastSeenText,
                peer.IsVisibleOnCurrentMap ? "当前地图可见" : peer.HasPosition ? "其他地图" : "在线，等待坐标")).ToArray()
            : [new SyncPeerItem("暂无同步成员", "--", "#6F8798", "--", "--", "离线")];
    }

    private static string FormatLocalSyncPortText(string localEndpoint)
    {
        var endpoint = localEndpoint?.Trim() ?? "";
        var separatorIndex = endpoint.LastIndexOf(':');
        var port = separatorIndex >= 0 && separatorIndex < endpoint.Length - 1
            ? endpoint[(separatorIndex + 1)..]
            : "39247";
        return port;
    }

    public void ApplyPeerMarkers(IReadOnlyList<NativePeerMarker> peers)
    {
        var activePeerIds = peers
            .Select(peer => peer.PeerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in peerElementsById.Keys.Where(id => !activePeerIds.Contains(id)).ToArray())
        {
            var staleMarker = peerElementsById[staleId];
            OverlayLayer.Children.Remove(staleMarker);
            peerElements.Remove(staleMarker);
            peerElementsById.Remove(staleId);
        }

        foreach (var peer in peers)
        {
            if (string.IsNullOrWhiteSpace(peer.PeerId))
            {
                continue;
            }

            if (peerElementsById.TryGetValue(peer.PeerId, out var marker))
            {
                if (ShouldRecreatePeerMarker(marker, peer))
                {
                    OverlayLayer.Children.Remove(marker);
                    peerElements.Remove(marker);
                    marker = CreatePeerMarker(peer);
                    peerElementsById[peer.PeerId] = marker;
                    peerElements.Add(marker);
                    OverlayLayer.Children.Add(marker);
                }
            }
            else
            {
                marker = CreatePeerMarker(peer);
                peerElementsById[peer.PeerId] = marker;
                peerElements.Add(marker);
                OverlayLayer.Children.Add(marker);
            }

            marker.Tag = peer;
            UpdateMarkerRotation(marker, peer.YawDegrees);
            ToolTipService.SetToolTip(marker, $"{peer.DisplayName} | {peer.LastSeenAt.LocalDateTime:HH:mm:ss}");
        }

        UpdateOverlayPositions();
    }

    public void SetPoiFilters(IReadOnlyList<PoiFilterOption> filters)
    {
        foreach (var filter in filters)
        {
            poiFilterState[filter.Kind] = filter.IsEnabled;
        }

        PoiFilterRepeater.ItemsSource = filters
            .Select(filter => new PoiFilterItem(
                filter.Kind,
                filter.Label,
                $"{filter.Label} ({filter.Count})",
                filter.IsEnabled,
                filter.Count > 0,
                $"Toggle native POI filter {filter.Label}"))
            .ToArray();
    }

    private void RenderOverlays()
    {
        if (map is null)
        {
            RemoveMapOverlayElements();
            return;
        }

        if (string.IsNullOrWhiteSpace(map.ImagePath) || !File.Exists(map.ImagePath))
        {
            RemoveMapOverlayElements();
            UpdateOverlayPositions();
            return;
        }

        EnsurePlayerPoint(map.PlayerPoint);
        var activePoiKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in map.Points)
        {
            var key = BuildPoiKey(map, point);
            activePoiKeys.Add(key);
            if (!poiElementsByKey.TryGetValue(key, out var marker))
            {
                marker = CreatePoiMarker(point);
                poiElementsByKey[key] = marker;
                OverlayLayer.Children.Add(marker);
            }

            marker.Tag = point;
            ToolTipService.SetToolTip(marker, point.Label);
        }

        foreach (var staleKey in poiElementsByKey.Keys.Where(key => !activePoiKeys.Contains(key)).ToArray())
        {
            var staleMarker = poiElementsByKey[staleKey];
            OverlayLayer.Children.Remove(staleMarker);
            poiElementsByKey.Remove(staleKey);
        }

        RebuildOverlayElementList();
        UpdateOverlayPositions();
    }

    private void AddPlayerPoint(MapPrototypePoint point)
    {
        EnsurePlayerPoint(point);
        RebuildOverlayElementList();
    }

    private void AddPoint(MapPrototypePoint point)
    {
        var marker = point.Kind == "player" ? CreatePlayerMarker(point) : CreatePoiMarker(point);
        marker.Tag = point;
        ToolTipService.SetToolTip(marker, point.Label);
        overlayElements.Add(marker);
        OverlayLayer.Children.Add(marker);
    }

    private void UpdatePlayerMarker(MapPrototypePoint point)
    {
        EnsurePlayerPoint(point);
        RebuildOverlayElementList();
        UpdateOverlayPositions();
    }

    private void EnsurePlayerPoint(MapPrototypePoint point)
    {
        if (playerMarker is null)
        {
            playerMarker = CreatePlayerMarker(point);
            OverlayLayer.Children.Insert(0, playerMarker);
        }

        playerMarker.Tag = point;
        UpdateMarkerRotation(playerMarker, point.YawDegrees);
        ToolTipService.SetToolTip(playerMarker, point.Label);
    }

    private void RemoveMapOverlayElements()
    {
        if (playerMarker is not null)
        {
            OverlayLayer.Children.Remove(playerMarker);
            playerMarker = null;
        }

        foreach (var marker in poiElementsByKey.Values)
        {
            OverlayLayer.Children.Remove(marker);
        }

        poiElementsByKey.Clear();
        overlayElements.Clear();
    }

    private void RebuildOverlayElementList()
    {
        overlayElements.Clear();
        if (playerMarker is not null)
        {
            overlayElements.Add(playerMarker);
        }

        overlayElements.AddRange(poiElementsByKey.Values);
    }

    private static string BuildPoiKey(MapPrototypeModel map, MapPrototypePoint point)
    {
        return string.Join(
            "|",
            map.Id,
            point.Kind,
            point.U.ToString("R", CultureInfo.InvariantCulture),
            point.V.ToString("R", CultureInfo.InvariantCulture),
            point.Label,
            point.IconName,
            point.LabelText,
            point.ShowLabel ? "1" : "0");
    }

    private static bool ShouldRecreatePeerMarker(FrameworkElement marker, NativePeerMarker peer)
    {
        return marker.Tag is not NativePeerMarker previous ||
            !string.Equals(previous.DisplayName, peer.DisplayName, StringComparison.Ordinal) ||
            !string.Equals(previous.Color, peer.Color, StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateMarkerRotation(FrameworkElement marker, double yawDegrees)
    {
        if (marker is not Panel panel)
        {
            return;
        }

        foreach (var child in panel.Children)
        {
            if (child is Polygon { RenderTransform: RotateTransform rotate })
            {
                rotate.Angle = ToMarkerRotation(yawDegrees);
                return;
            }
        }
    }

    private static double ToMarkerRotation(double yawDegrees)
    {
        return (yawDegrees + MarkerYawVisualOffsetDegrees) % 360;
    }

    private ImageSource CreateImageSource(string imagePath)
    {
        var modifiedUtcTicks = File.Exists(imagePath) ? File.GetLastWriteTimeUtc(imagePath).Ticks : 0;
        var cacheKey = $"{imagePath}|{modifiedUtcTicks}";
        if (imageSourceCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        ImageSource source = string.Equals(System.IO.Path.GetExtension(imagePath), ".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImageSource(new Uri(imagePath))
            : new BitmapImage(new Uri(imagePath));
        imageSourceCache[cacheKey] = source;
        return source;
    }

    private static Uri ResolveMapIconUri(string iconName)
    {
        var localPath = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "map-icons", $"{iconName}.png");
        return System.IO.File.Exists(localPath)
            ? new Uri(localPath)
            : new Uri($"{WebMapIconBaseUrl}/{iconName}.png");
    }

    private FrameworkElement CreatePoiMarker(MapPrototypePoint point)
    {
        var iconName = GetPoiIconName(point);
        if (!string.IsNullOrWhiteSpace(iconName))
        {
            return CreateWebMapIconMarker(point, ResolveMapIconUri(iconName), 26);
        }

        var color = point.Kind switch
        {
            "label" => Colors.DeepSkyBlue,
            "spawnPmc" => Colors.MediumPurple,
            "spawnScav" => Colors.MediumTurquoise,
            "spawnBoss" => Colors.Orchid,
            "spawnSniper" => Colors.LightCoral,
            "spawnRogue" => Colors.SandyBrown,
            "locks" => Colors.Gold,
            "switches" => Colors.Orange,
            "hazards" => Colors.IndianRed,
            "stationary" => Colors.LightSteelBlue,
            "btr" => Colors.CadetBlue,
            "transits" => Colors.Cyan,
            _ => Colors.DodgerBlue
        };
        var grid = new Grid
        {
            Width = 26,
            Height = 26
        };
        AutomationProperties.SetName(grid, $"Native POI {point.Kind}: {point.Label}");

        grid.Children.Add(new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        return grid;
    }

    private FrameworkElement CreateWebMapIconMarker(MapPrototypePoint point, Uri iconUri, double size)
    {
        var canvas = new Canvas
        {
            Width = size,
            Height = size
        };
        AutomationProperties.SetName(canvas, $"Native POI {point.Kind}: {point.Label}");

        canvas.Children.Add(new Image
        {
            Width = size,
            Height = size,
            Source = GetMarkerIconSource(iconUri),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (point.ShowLabel)
        {
            canvas.Children.Add(CreatePoiLabel(point, size));
        }

        return canvas;
    }

    private ImageSource GetMarkerIconSource(Uri iconUri)
    {
        var key = iconUri.AbsoluteUri;
        if (markerIconSourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = new BitmapImage(iconUri);
        markerIconSourceCache[key] = source;
        return source;
    }

    private static string GetPoiIconName(MapPrototypePoint point)
    {
        if (!string.IsNullOrWhiteSpace(point.IconName))
        {
            return point.IconName;
        }

        if (string.Equals(point.Kind, "extract", StringComparison.OrdinalIgnoreCase))
        {
            return "extract_shared";
        }

        return string.Equals(point.Kind, "transits", StringComparison.OrdinalIgnoreCase) ? "extract_transit" : "";
    }

    private static FrameworkElement CreatePoiLabel(MapPrototypePoint point, double iconSize)
    {
        var text = string.IsNullOrWhiteSpace(point.LabelText) ? point.Label : point.LabelText;
        var label = new Border
        {
            Padding = new Thickness(5, 2, 5, 3),
            Background = new SolidColorBrush(Colors.Black) { Opacity = 0.68 },
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            }
        };

        var left = iconSize * 0.72;
        if (point.U > 0.92)
        {
            left = -180;
            label.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else if (point.U < 0.08)
        {
            left = iconSize * 0.72;
        }

        var top = iconSize * 0.6;
        if (point.V > 0.9)
        {
            top = -24;
        }

        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        return label;
    }

    private static FrameworkElement CreatePlayerMarker(MapPrototypePoint point)
    {
        var grid = new Grid
        {
            Width = 34,
            Height = 44
        };
        AutomationProperties.SetName(grid, $"Native player: {point.Label}");

        grid.Children.Add(new Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = new SolidColorBrush(Colors.Orange),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var arrow = new Polygon
        {
            Points =
            [
                new Point(17, 0),
                new Point(24, 14),
                new Point(10, 14)
            ],
            Fill = new SolidColorBrush(Colors.OrangeRed),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = ToMarkerRotation(point.YawDegrees) }
        };
        grid.Children.Add(arrow);

        return grid;
    }

    private static FrameworkElement CreatePeerMarker(NativePeerMarker peer)
    {
        var color = ParseHexColor(peer.Color, Colors.DeepSkyBlue);
        var grid = new Grid
        {
            Width = 38,
            Height = 46
        };
        AutomationProperties.SetName(grid, $"Native LAN peer: {peer.DisplayName}");

        grid.Children.Add(new Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var arrow = new Polygon
        {
            Points =
            [
                new Point(19, 0),
                new Point(27, 15),
                new Point(11, 15)
            ],
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = ToMarkerRotation(peer.YawDegrees) }
        };
        grid.Children.Add(arrow);

        var label = new Border
        {
            MinWidth = 38,
            Padding = new Thickness(4, 1, 4, 2),
            Background = new SolidColorBrush(Colors.Black) { Opacity = 0.64 },
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = peer.DisplayName,
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 72
            }
        };
        grid.Children.Add(label);

        return grid;
    }

    private static global::Windows.UI.Color ParseHexColor(string value, global::Windows.UI.Color fallback)
    {
        var text = value.Trim().TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return fallback;
        }

        return global::Windows.UI.Color.FromArgb(
            255,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
    }

    private void FitToViewport(bool resetView)
    {
        if (map is null || Viewport.ActualWidth <= 0)
        {
            return;
        }

        var mapAspectRatio = map.AspectRatio;
        if (!double.IsFinite(mapAspectRatio) || mapAspectRatio <= 0)
        {
            mapAspectRatio = WebMapViewportAspectRatio;
        }

        var viewportWidth = Math.Max(MinimumViewportWidth, Viewport.ActualWidth);
        var viewportHeight = Math.Max(MinimumViewportHeight, viewportWidth / mapAspectRatio);
        if (!double.IsFinite(viewportHeight))
        {
            return;
        }

        isFittingViewport = true;
        try
        {
            if (Math.Abs(Viewport.Height - viewportHeight) > 0.5)
            {
                Viewport.Height = viewportHeight;
            }

            mapWidth = viewportWidth;
            mapHeight = viewportHeight;
            mapOffsetX = 0;
            mapOffsetY = 0;

            MapViewportSurface.Width = mapWidth;
            MapViewportSurface.Height = mapHeight;
            MapViewportSurface.Margin = new Thickness(mapOffsetX, mapOffsetY, 0, 0);
            MapViewportSurface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, mapWidth, mapHeight) };
            ViewportContent.Clip = new RectangleGeometry { Rect = new Rect(0, 0, viewportWidth, viewportHeight) };
            MapImage.Width = mapWidth;
            MapImage.Height = mapHeight;
            MapLayer.Width = mapWidth;
            MapLayer.Height = mapHeight;
            OverlayLayer.Width = mapWidth;
            OverlayLayer.Height = mapHeight;
            fitScale = 1;
            if (resetView)
            {
                scale = fitScale;
                translateX = 0;
                translateY = 0;
            }
            else
            {
                scale = Math.Max(scale, fitScale);
                ClampTranslation();
            }

            ApplyTransform();
        }
        finally
        {
            isFittingViewport = false;
        }
    }

    private void SetZoom(double nextScale, Point origin)
    {
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(nextScale, fitScale, 5.0);
        var previousScale = scale;
        if (Math.Abs(previousScale - clamped) < 0.001)
        {
            return;
        }

        var surfaceOrigin = ToMapViewportSurfacePoint(origin);
        var mapX = (surfaceOrigin.X - translateX) / previousScale;
        var mapY = (surfaceOrigin.Y - translateY) / previousScale;
        scale = clamped;
        translateX = surfaceOrigin.X - mapX * scale;
        translateY = surfaceOrigin.Y - mapY * scale;
        ClampTranslation();
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        MapTransform.ScaleX = scale;
        MapTransform.ScaleY = scale;
        MapTransform.TranslateX = translateX;
        MapTransform.TranslateY = translateY;
        UpdateOverlayPositions();
        if (string.IsNullOrWhiteSpace(pinnedStatusMessage))
        {
            UpdateStatusTexts("就绪");
        }
    }

    private void UpdateStatusTexts(string state = "就绪")
    {
        if (map is null)
        {
            MapStatusText.Text = $"地图状态: {state}";
            PoiCountText.Text = "0 POI";
            return;
        }

        var sourceLabel = poiSourceOptions.FirstOrDefault(option => option.Value == map.PoiSource)?.Label ?? map.PoiSource;
        var baseLabel = GetSelectedMapBaseLabel(map.MapBase);
        var mapModeText = autoMapEnabled ? "自动" : "手动";
        var autoRefreshText = autoRefreshEnabled ? "On" : "Off";
        var autoMapText = autoMapEnabled ? "On" : "Off";
        var pipText = pipEnabled ? "On" : "Off";
        var activeFilterCount = poiFilterState.Count(item => item.Value);
        MapStatusText.Text = $"地图状态: {map.Name} / {mapModeText}";
        PoiCountText.Text = showPoi ? $"{map.Points.Count} POI" : "POI hidden";
        var status = $"{state} | 底图: {baseLabel} | 来源: {sourceLabel} | 筛选: {activeFilterCount} 类 | 缩放: {scale:0.00}x | 自动刷新: {autoRefreshText} | 自动地图: {autoMapText} | 画中画: {pipText} | {startupPageLabel}";
        if (!string.Equals(StatusLineText.Text, status, StringComparison.Ordinal))
        {
            StatusLineText.Text = status;
        }
    }

    private void UpdateOverlayPositions()
    {
        foreach (var element in overlayElements)
        {
            if (element.Tag is not MapPrototypePoint point)
            {
                continue;
            }

            var x = translateX + point.U * mapWidth * scale;
            var y = translateY + point.V * mapHeight * scale;
            Canvas.SetLeft(element, x - element.Width / 2.0);
            Canvas.SetTop(element, y - element.Height / 2.0);
        }

        foreach (var element in peerElements)
        {
            if (element.Tag is not NativePeerMarker peer)
            {
                continue;
            }

            var x = translateX + peer.U * mapWidth * scale;
            var y = translateY + peer.V * mapHeight * scale;
            Canvas.SetLeft(element, x - element.Width / 2.0);
            Canvas.SetTop(element, y - element.Height / 2.0);
        }
    }

    private void UpdateRaidSnapshot(MapProjectionResult result, bool isListening)
    {
        if (!result.HasCoordinate)
        {
            watchStatusLabel = isListening ? "等待截图" : "未监听";
            latestCoordinateLabel = "暂无截图坐标";
        }
        else if (result.Coordinate is not null)
        {
            watchStatusLabel = result.IsInBounds ? "已读取坐标" : "坐标不在当前地图";
            latestCoordinateLabel = FormatCoordinate(result.Coordinate);
        }

        UpdateRaidInfoText();
    }

    private void UpdateRaidInfoText()
    {
        var mapName = map?.Name ?? "--";
        var mapMode = autoMapEnabled ? "自动地图" : "手动地图";
        var parts = new[]
        {
            $"监听状态: {watchStatusLabel}",
            logStatusLabel,
            raidStateLabel,
            $"当前地图: {mapName} ({mapMode})",
            logMapLabel,
            latestLogLabel,
            latestCoordinateLabel,
            latestLogEventLabel
        }.Where(part => !string.IsNullOrWhiteSpace(part));
        var detail = string.Join(" | ", parts);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            AutomationProperties.SetName(RaidDetailsPanel, detail);
        }
        UpdateRaidSummary(latestRaidSnapshot);
    }

    private void UpdateRaidSummary(RaidLogSnapshot snapshot)
    {
        var mapName = !string.IsNullOrWhiteSpace(snapshot.MapName) ? snapshot.MapName : map?.Name ?? "--";
        RaidSummaryRepeater.ItemsSource = new[]
        {
            new RaidDetailItem("监听", watchStatusLabel),
            new RaidDetailItem("当前地图", mapName),
            new RaidDetailItem("模式", SafeText(snapshot.SessionMode, "--")),
            new RaidDetailItem("战局状态", FormatRaidState(snapshot.RaidState)),
            new RaidDetailItem("最近日志", FormatClock(snapshot.LastScanAt))
        };

        RaidCurrentRepeater.ItemsSource = new[]
        {
            new RaidDetailItem("当前地图", mapName),
            new RaidDetailItem("会话模式", SafeText(snapshot.SessionMode, "未知")),
            new RaidDetailItem("Raid ID", SafeText(snapshot.RaidId)),
            new RaidDetailItem("Location", SafeText(snapshot.Location)),
            new RaidDetailItem("状态", FormatRaidState(snapshot.RaidState))
        };

        var server = string.IsNullOrWhiteSpace(snapshot.ServerIp)
            ? "--"
            : string.IsNullOrWhiteSpace(snapshot.ServerPort) ? snapshot.ServerIp : $"{snapshot.ServerIp}:{snapshot.ServerPort}";
        RaidTimingRepeater.ItemsSource = new[]
        {
            new RaidDetailItem("最近日志", FormatClock(snapshot.LastScanAt)),
            new RaidDetailItem("排队耗时", FormatDuration(snapshot.QueueDurationSeconds)),
            new RaidDetailItem("加载耗时", FormatDuration(snapshot.LoadDurationSeconds)),
            new RaidDetailItem("进入战局", FormatClock(snapshot.GameStartAt)),
            new RaidDetailItem("结束时间", FormatClock(snapshot.RaidEndAt)),
            new RaidDetailItem("服务器", server)
        };

        var events = snapshot.RecentEvents.Count > 0
            ? snapshot.RecentEvents
                .OrderByDescending(item => item.Timestamp)
                .Select(item => new RaidEventItem(item.Title, item.Detail, FormatClock(item.Timestamp)))
                .ToArray()
            : [new RaidEventItem("暂无战局事件", snapshot.LastEventDetail ?? "等待 Log 或截图监听", "--")];
        RaidEventsRepeater.ItemsSource = events;
    }

    private static string FormatRaidState(RaidState state)
    {
        return state switch
        {
            RaidState.Waiting => "等待中",
            RaidState.Matching => "匹配中",
            RaidState.Loading => "加载中",
            RaidState.InRaid => "战局中",
            RaidState.Ended => "已结束",
            RaidState.Aborted => "已取消",
            _ => "未知"
        };
    }

    private static string SafeText(string? value, string fallback = "--")
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string FormatClock(DateTimeOffset? timestamp)
    {
        return timestamp is null ? "--" : timestamp.Value.LocalDateTime.ToString("HH:mm:ss");
    }

    private static string FormatDuration(double? seconds)
    {
        return seconds is null || !double.IsFinite(seconds.Value) ? "--" : $"{seconds.Value:0.0}s";
    }

    private static string FormatCoordinate(ScreenshotCoordinate coordinate)
    {
        var timestamp = coordinate.ModifiedAt > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(coordinate.ModifiedAt).LocalDateTime.ToString("HH:mm:ss")
            : "--:--:--";
        return $"最近截图 {timestamp} | x {coordinate.X:0.0}, y {coordinate.Y:0.0}, z {coordinate.Z:0.0}";
    }

    private static string FormatPathLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "未选择";
        }

        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string ReadString(JsonObject payload, string propertyName, string fallback)
    {
        try
        {
            return payload[propertyName]?.GetValue<string>()?.Trim() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static double ReadDouble(JsonObject payload, string propertyName, double min, double max, double fallback)
    {
        try
        {
            var value = payload[propertyName]?.GetValue<double>();
            return value is not null && double.IsFinite(value.Value)
                ? Math.Clamp(value.Value, min, max)
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonObject payload, string propertyName, int min, int max, int fallback)
    {
        try
        {
            var value = payload[propertyName]?.GetValue<int>();
            return value is null ? fallback : Math.Clamp(value.Value, min, max);
        }
        catch
        {
            return fallback;
        }
    }

    private void ClampTranslation()
    {
        var viewportWidth = mapWidth;
        var viewportHeight = mapHeight;
        var scaledWidth = mapWidth * scale;
        var scaledHeight = mapHeight * scale;

        if (scale <= 1 + 0.000001)
        {
            translateX = 0;
            translateY = 0;
            return;
        }

        translateX = scaledWidth <= viewportWidth
            ? 0
            : Math.Clamp(translateX, viewportWidth - scaledWidth, 0);
        translateY = scaledHeight <= viewportHeight
            ? 0
            : Math.Clamp(translateY, viewportHeight - scaledHeight, 0);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (isFittingViewport)
        {
            return;
        }

        ViewportContent.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight)
        };
        FitToViewport(resetView: false);
    }

    private Point ToMapViewportSurfacePoint(Point viewportPoint)
    {
        return new Point(viewportPoint.X - mapOffsetX, viewportPoint.Y - mapOffsetY);
    }

    private void OnViewportPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        pinnedStatusMessage = null;
        var point = e.GetCurrentPoint(Viewport);
        var delta = point.Properties.MouseWheelDelta;
        var zoomFactor = Math.Exp(delta * 0.0012);
        SetZoom(scale * zoomFactor, point.Position);
        e.Handled = true;
    }

    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        isDragging = true;
        dragPointerId = point.PointerId;
        lastPointerPosition = point.Position;
        Viewport.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        if (!isDragging || dragPointerId != point.PointerId)
        {
            return;
        }

        pinnedStatusMessage = null;
        var dx = point.Position.X - lastPointerPosition.X;
        var dy = point.Position.Y - lastPointerPosition.Y;
        if (scale > 1 + 0.000001)
        {
            translateX += dx;
            translateY += dy;
            ClampTranslation();
            ApplyTransform();
        }

        lastPointerPosition = point.Position;
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragPointerId == e.GetCurrentPoint(Viewport).PointerId)
        {
            isDragging = false;
            dragPointerId = null;
            Viewport.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnFitClicked(object sender, RoutedEventArgs e)
    {
        pinnedStatusMessage = null;
        FitToViewport(resetView: true);
    }

    private void OnRefreshPositionClicked(object sender, RoutedEventArgs e)
    {
        RefreshPositionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPickScreenshotDirectoryClicked(object sender, RoutedEventArgs e)
    {
        PickScreenshotDirectoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPickLogDirectoryClicked(object sender, RoutedEventArgs e)
    {
        PickLogDirectoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleWatchClicked(object sender, RoutedEventArgs e)
    {
        ToggleWatchRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRaidDetailsClicked(object sender, RoutedEventArgs e)
    {
        SetRaidDetailsExpanded(!raidDetailsExpanded);
        RaidDetailsChanged?.Invoke(this, raidDetailsExpanded);
    }

    private void OnAutoRefreshClicked(object sender, RoutedEventArgs e)
    {
        autoRefreshEnabled = !autoRefreshEnabled;
        AutoRefreshButton.Content = autoRefreshEnabled ? "自动刷新: On" : "自动刷新: Off";
        pinnedStatusMessage = null;
        AutoRefreshChanged?.Invoke(this, autoRefreshEnabled);
        ApplyTransform();
    }

    private void OnAutoMapClicked(object sender, RoutedEventArgs e)
    {
        SetAutoMapEnabled(!autoMapEnabled);
        AutoMapChanged?.Invoke(this, autoMapEnabled);
    }

    private void OnPipClicked(object sender, RoutedEventArgs e)
    {
        SetPipEnabled(!pipEnabled);
        PipChanged?.Invoke(this, pipEnabled);
        ApplyTransform();
    }

    private void OnShowPoiClicked(object sender, RoutedEventArgs e)
    {
        SetShowPoiEnabled(!showPoi);
        ShowPoiChanged?.Invoke(this, showPoi);
    }

    private void OnStartupPageClicked(object sender, RoutedEventArgs e)
    {
        SetStartupPagePreference(true);
        StartupPageChanged?.Invoke(this, true);
    }

    private void OnCheckMapCacheClicked(object sender, RoutedEventArgs e)
    {
        CheckMapCacheRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMapSelectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressMapSelectionChanged || MapSelector.SelectedItem is not MapSelectorOption selectedOption)
        {
            return;
        }

        if (selectedOption.IsAuto)
        {
            if (!autoMapEnabled)
            {
                SetAutoMapEnabled(true);
                AutoMapChanged?.Invoke(this, true);
            }

            return;
        }

        if (autoMapEnabled)
        {
            SetAutoMapEnabled(false);
            AutoMapChanged?.Invoke(this, false);
        }

        MapSelectionRequested?.Invoke(this, selectedOption.MapId);
    }

    private void OnMapBaseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressMapBaseSelectionChanged || MapBaseSelector.SelectedItem is not MapBaseOption selectedBase)
        {
            return;
        }

        MapBaseSelectionRequested?.Invoke(this, selectedBase.Value);
    }

    private void OnPoiSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPoiSourceSelectionChanged || PoiSourceSelector.SelectedItem is not PoiSourceOption selectedSource)
        {
            return;
        }

        PoiSourceSelectionRequested?.Invoke(this, selectedSource.Value);
    }

    private void OnPoiFilterClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string kind)
        {
            return;
        }

        poiFilterState[kind] = checkBox.IsChecked == true;
        pinnedStatusMessage = null;
        PoiFiltersChanged?.Invoke(this, new Dictionary<string, bool>(poiFilterState, StringComparer.OrdinalIgnoreCase));
        ApplyTransform();
    }

    private void OnSyncHostClicked(object sender, RoutedEventArgs e)
    {
        syncMode = "host";
        SyncRemoteEndpointTextBox.IsEnabled = false;
        RaiseLanSyncConfigChanged();
        LanSyncStartRequested?.Invoke(this, CurrentLanSyncRequest);
    }

    private void OnSyncJoinClicked(object sender, RoutedEventArgs e)
    {
        syncMode = "join";
        SyncRemoteEndpointTextBox.IsEnabled = true;
        RaiseLanSyncConfigChanged();
        LanSyncStartRequested?.Invoke(this, CurrentLanSyncRequest);
    }

    private void OnSyncStopClicked(object sender, RoutedEventArgs e)
    {
        LanSyncStopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSyncConfigChanged(object sender, TextChangedEventArgs e)
    {
        RaiseLanSyncConfigChanged();
    }

    private void OnSyncConfigSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseLanSyncConfigChanged();
    }

    private void OnScreenFilterPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressScreenFilterConfigChanged ||
            ScreenFilterPresetComboBox.SelectedItem is not ScreenFilterPresetOption selectedPreset)
        {
            return;
        }

        if (!string.Equals(selectedPreset.Id, "custom", StringComparison.Ordinal))
        {
            suppressScreenFilterConfigChanged = true;
            try
            {
                SetScreenFilterValues(selectedPreset.Preset);
            }
            finally
            {
                suppressScreenFilterConfigChanged = false;
            }
        }

        ScreenFilterStatusText.Text = "屏幕滤镜: 参数已更新";
        RaiseScreenFilterConfigChanged();
    }

    private void OnScreenFilterDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseScreenFilterConfigChanged();
    }

    private void OnScreenFilterNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!suppressScreenFilterConfigChanged)
        {
            suppressScreenFilterConfigChanged = true;
            try
            {
                SelectScreenFilterPreset("custom");
            }
            finally
            {
                suppressScreenFilterConfigChanged = false;
            }
        }

        RaiseScreenFilterConfigChanged();
    }

    private void OnScreenFilterApplyClicked(object sender, RoutedEventArgs e)
    {
        ScreenFilterApplyRequested?.Invoke(this, CurrentScreenFilterRequest);
    }

    private void OnScreenFilterResetClicked(object sender, RoutedEventArgs e)
    {
        ScreenFilterResetRequested?.Invoke(this, CurrentScreenFilterRequest);
    }

    private void RaiseLanSyncConfigChanged()
    {
        if (suppressLanSyncConfigChanged)
        {
            return;
        }

        LanSyncConfigChanged?.Invoke(this, CurrentLanSyncRequest);
    }

    private void RaiseScreenFilterConfigChanged()
    {
        if (suppressScreenFilterConfigChanged)
        {
            return;
        }

        ScreenFilterConfigChanged?.Invoke(this, CurrentScreenFilterRequest);
    }

    private void OnZoomInClicked(object sender, RoutedEventArgs e)
    {
        pinnedStatusMessage = null;
        SetZoom(scale * 1.25, new Point(Viewport.ActualWidth / 2.0, Viewport.ActualHeight / 2.0));
    }

    private void OnZoomOutClicked(object sender, RoutedEventArgs e)
    {
        pinnedStatusMessage = null;
        SetZoom(scale / 1.25, new Point(Viewport.ActualWidth / 2.0, Viewport.ActualHeight / 2.0));
    }

    private string GetSelectedSyncColor()
    {
        return SyncColorComboBox.SelectedItem is ComboBoxItem item && item.Content is not null
            ? item.Content.ToString() ?? "#4fd1ff"
            : "#4fd1ff";
    }

    private void SelectSyncColor(string color)
    {
        var normalized = string.IsNullOrWhiteSpace(color) ? "#4fd1ff" : color.Trim();
        for (var index = 0; index < SyncColorComboBox.Items.Count; index++)
        {
            if (SyncColorComboBox.Items[index] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                SyncColorComboBox.SelectedIndex = index;
                return;
            }
        }

        SyncColorComboBox.SelectedIndex = 0;
    }

    private void SetScreenFilterValues(ScreenFilterPreset preset)
    {
        ScreenFilterGammaBox.Value = Math.Clamp(preset.Gamma, 0.2, 5.0);
        ScreenFilterBrightnessBox.Value = Math.Clamp(preset.Brightness, -100, 100);
        ScreenFilterContrastBox.Value = Math.Clamp(preset.Contrast, -100, 100);
        ScreenFilterRedBox.Value = Math.Clamp(preset.Red, 0, 255);
        ScreenFilterGreenBox.Value = Math.Clamp(preset.Green, 0, 255);
        ScreenFilterBlueBox.Value = Math.Clamp(preset.Blue, 0, 255);
    }

    private void SelectScreenFilterPreset(string presetId)
    {
        var normalized = string.IsNullOrWhiteSpace(presetId) ? "default" : presetId.Trim();
        ScreenFilterPresetComboBox.SelectedItem = screenFilterPresetOptions.FirstOrDefault(
            preset => string.Equals(preset.Id, normalized, StringComparison.OrdinalIgnoreCase)) ?? screenFilterPresetOptions[0];
    }

    private void SelectScreenFilterDisplay(string displayName)
    {
        var normalized = displayName?.Trim() ?? "";
        ScreenFilterDisplayComboBox.SelectedItem = screenFilterDisplayOptions.FirstOrDefault(
            display => string.Equals(display.DisplayName, normalized, StringComparison.OrdinalIgnoreCase)) ??
            screenFilterDisplayOptions[0];
    }

    private static double ReadScreenFilterNumber(NumberBox numberBox, double min, double max, double fallback)
    {
        return double.IsFinite(numberBox.Value)
            ? Math.Clamp(numberBox.Value, min, max)
            : fallback;
    }

    private sealed record PoiSourceOption(string Value, string Label);

    private sealed record ScreenFilterDisplayOption(string DisplayName, string Label);

    private sealed record MapSelectorOption(string MapId, string DisplayName, bool IsAuto)
    {
        public static MapSelectorOption Auto { get; } = new("", "自动选择地图", true);

        public static MapSelectorOption FromMap(MapPrototypeModel map) => new(map.Id, map.Name, false);
    }

    private string GetSelectedMapBaseLabel(string value)
    {
        if (MapBaseSelector.ItemsSource is IEnumerable<MapBaseOption> options)
        {
            return options.FirstOrDefault(option => option.Value == value)?.Label ?? value;
        }

        return value;
    }

    private sealed record PoiFilterItem(
        string Kind,
        string Label,
        string DisplayLabel,
        bool IsEnabled,
        bool HasItems,
        string AutomationName);

    private sealed record RaidDetailItem(string Label, string Value);

    private sealed record RaidEventItem(string Title, string Detail, string Time);

    private sealed record SyncPeerItem(
        string DisplayName,
        string RoleText,
        string Color,
        string MapName,
        string LastSeenText,
        string VisibilityStatus);

    private static string GetPoiKindLabel(string kind)
    {
        return kind switch
        {
            "extract" => "Extracts",
            "label" => "Labels",
            "spawnPmc" => "PMC spawns",
            "spawnScav" => "Scav spawns",
            "spawnBoss" => "Boss",
            "spawnSniper" => "Sniper Scav",
            "spawnRogue" => "Rogue/Bot PMC",
            "locks" => "Locks",
            "switches" => "Switches",
            "hazards" => "Hazards",
            "stationary" => "Stationary",
            "btr" => "BTR",
            "transits" => "Transits",
            _ => kind
        };
    }
}
