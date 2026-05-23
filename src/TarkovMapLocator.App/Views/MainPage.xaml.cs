using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json.Nodes;
using TarkovMapLocator.App.Models;
using TarkovMapLocator.App.Pip;
using TarkovMapLocator.App.Services;
using TarkovMapLocator.App.ViewModels;
using TarkovMapLocator.Core.Logs;
using TarkovMapLocator.Core.Maps;

namespace TarkovMapLocator.App.Views;

public partial class MainPage : Page
{
    private readonly DispatcherQueueTimer nativeMapAutoRefreshTimer;
    private readonly DispatcherQueueTimer nativeMapWatchTimer;
    private LocalPathConfigService appConfigService = new();
    private PythonBackendProcessService? backendProcessService;
    private NativeFolderPickerService? folderPickerService;
    private CancellationTokenSource? startupCancellation;
    private readonly MapPrototypeViewModel mapPrototypeViewModel = new();
    private readonly MapImageCacheService mapImageCacheService = new();
    private readonly RaidLogMonitorService raidLogMonitorService = new();
    private readonly LanPeerSyncService lanPeerSyncService = new();
    private readonly NativePathAutoDetectService pathAutoDetectService = new();
    private readonly ScreenGammaService screenGammaService = new();
    private readonly DispatcherQueueTimer nativeLanSyncTimer;
    private ScreenshotCoordinate? latestNativeCoordinate;
    private MapPrototypePoint? latestNativePlayerPoint;
    private NativeMapPipWindow? nativeMapPipWindow;
    private bool nativeMapLoaded;
    private bool isRefreshingRaidLog;

    public MainPage()
    {
        InitializeComponent();
        NativeMapPrototype.PickScreenshotDirectoryRequested += OnNativeMapPickScreenshotDirectoryRequested;
        NativeMapPrototype.PickLogDirectoryRequested += OnNativeMapPickLogDirectoryRequested;
        NativeMapPrototype.ToggleWatchRequested += OnNativeMapToggleWatchRequested;
        NativeMapPrototype.RefreshPositionRequested += OnNativeMapRefreshPositionRequested;
        NativeMapPrototype.AutoRefreshChanged += OnNativeMapAutoRefreshChanged;
        NativeMapPrototype.AutoMapChanged += OnNativeMapAutoMapChanged;
        NativeMapPrototype.PipChanged += OnNativeMapPipChanged;
        NativeMapPrototype.ShowPoiChanged += OnNativeMapShowPoiChanged;
        NativeMapPrototype.StartupPageChanged += OnNativeMapStartupPageChanged;
        NativeMapPrototype.MapSelectionRequested += OnNativeMapSelectionRequested;
        NativeMapPrototype.MapBaseSelectionRequested += OnNativeMapBaseSelectionRequested;
        NativeMapPrototype.PoiSourceSelectionRequested += OnNativeMapPoiSourceSelectionRequested;
        NativeMapPrototype.PoiFiltersChanged += OnNativeMapPoiFiltersChanged;
        NativeMapPrototype.RaidDetailsChanged += OnNativeMapRaidDetailsChanged;
        NativeMapPrototype.CheckMapCacheRequested += OnNativeMapCheckCacheRequested;
        NativeMapPrototype.LanSyncConfigChanged += OnNativeMapLanSyncConfigChanged;
        NativeMapPrototype.LanSyncStartRequested += OnNativeMapLanSyncStartRequested;
        NativeMapPrototype.LanSyncStopRequested += OnNativeMapLanSyncStopRequested;
        NativeMapPrototype.ScreenFilterConfigChanged += OnScreenFilterConfigChanged;
        NativeMapPrototype.ScreenFilterApplyRequested += OnScreenFilterApplyRequested;
        NativeMapPrototype.ScreenFilterResetRequested += OnScreenFilterResetRequested;
        nativeMapAutoRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        nativeMapAutoRefreshTimer.Interval = TimeSpan.FromSeconds(2);
        nativeMapAutoRefreshTimer.Tick += OnNativeMapAutoRefreshTick;
        nativeMapWatchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        nativeMapWatchTimer.Interval = TimeSpan.FromSeconds(2);
        nativeMapWatchTimer.Tick += OnNativeMapWatchTick;
        nativeLanSyncTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        nativeLanSyncTimer.Interval = TimeSpan.FromSeconds(1);
        nativeLanSyncTimer.Tick += OnNativeLanSyncTick;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var services = e.Parameter as MainPageServices
            ?? throw new InvalidOperationException("Page services were not provided.");
        backendProcessService = services.BackendProcessService;
        folderPickerService = services.FolderPickerService;
        appConfigService = services.LocalPathConfigService;

        startupCancellation = new CancellationTokenSource();
        await StartBackendAndNavigateAsync(startupCancellation.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ShutdownNativeMapFeatures();
        base.OnNavigatedFrom(e);
    }

    public void ShutdownNativeMapFeatures()
    {
        nativeMapAutoRefreshTimer.Stop();
        nativeMapWatchTimer.Stop();
        nativeLanSyncTimer.Stop();
        CloseNativeMapPipWindow(saveDisabled: true);
        screenGammaService.ResetAll();
        _ = lanPeerSyncService.StopAsync();
        startupCancellation?.Cancel();
        startupCancellation?.Dispose();
        startupCancellation = null;
    }

    public async Task ShutdownNativeMapFeaturesAsync()
    {
        ShutdownNativeMapFeatures();
        await lanPeerSyncService.StopAsync();
    }

    private async Task StartBackendAndNavigateAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusText.Text = "正在启动 Python 本地 API 服务...";
            await backendProcessService!.StartAndWaitAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            StatusText.Text = "正在加载原生界面...";
            ModeBar.Visibility = Visibility.Visible;
            LoadingView.Visibility = Visibility.Collapsed;
            ShowNativeMap();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"启动失败：{ex.Message}";
        }
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowUiError(ex);
        }
    }

    private void ShowUiError(Exception ex)
    {
        var message = $"操作失败: {ex.Message}";
        try
        {
            NativeMapPrototype.ShowStatus(message);
        }
        catch
        {
            StatusText.Text = message;
        }
    }

    private void OnNativeMapModeClicked(object sender, RoutedEventArgs e)
    {
        ShowNativeMap();
    }

    private async void OnMarketModeClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            StopNativeMapAutoRefresh();
            NativeMapPrototype.Visibility = Visibility.Collapsed;
            NativeMarket.Visibility = Visibility.Visible;
            NativeTracker.Visibility = Visibility.Collapsed;
            await NativeMarket.EnsureLoadedAsync(
                appConfigService,
                startupCancellation?.Token ?? CancellationToken.None);
        });
    }

    private async void OnTrackerModeClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            StopNativeMapAutoRefresh();
            NativeMapPrototype.Visibility = Visibility.Collapsed;
            NativeMarket.Visibility = Visibility.Collapsed;
            NativeTracker.Visibility = Visibility.Visible;
            await NativeTracker.EnsureLoadedAsync(
                appConfigService,
                startupCancellation?.Token ?? CancellationToken.None);
        });
    }

    private void ShowNativeMap()
    {
        EnsureNativeMapLoaded();
        ApplyNativeMapRuntimePreferences(appConfigService.ReadUiPreferences());
        NativeMapPrototype.Visibility = Visibility.Visible;
        NativeMarket.Visibility = Visibility.Collapsed;
        NativeTracker.Visibility = Visibility.Collapsed;
        if (NativeMapPrototype.IsAutoRefreshEnabled)
        {
            nativeMapWatchTimer.Stop();
            NativeMapPrototype.SetWatchEnabled(false);
            nativeMapAutoRefreshTimer.Start();
            RefreshNativeMapPosition();
            _ = RefreshNativeRaidLogAsync();
        }
    }

    private void EnsureNativeMapLoaded()
    {
        if (nativeMapLoaded)
        {
            return;
        }

        RefreshNativeMapListFromMetadata(allowMapReload: false);
        ApplyNativeMapPreferences(appConfigService.ReadUiPreferences());
        AutoDetectNativeLocalPaths();
        RefreshNativeMapLocalState();
        _ = RefreshNativeRaidLogAsync();
        NativeMapPrototype.SetAvailableMaps(
            mapPrototypeViewModel.AvailableMaps,
            mapPrototypeViewModel.CurrentMap.Id);
        NativeMapPrototype.SetMapBaseOptions(
            mapPrototypeViewModel.MapBaseOptions,
            mapPrototypeViewModel.CurrentMapBase);
        NativeMapPrototype.LoadMap(mapPrototypeViewModel.CurrentMap);
        NativeMapPrototype.SetPoiFilters(mapPrototypeViewModel.PoiFilters);
        NativeMapPrototype.SetStartupPagePreference(true);
        RefreshNativeLanPeerUi();
        RefreshScreenFilterUi();
        nativeMapLoaded = true;
    }

    private void ApplyNativeMapPreferences(JsonObject preferences)
    {
        var poiSource = ReadPreferenceString(preferences, "nativePoiSource", "extracts");
        var mapBase = ReadPreferenceString(preferences, "nativeMapBase", "cache");
        var mapId = ReadPreferenceString(preferences, "nativeMapId");
        var showPoi = ReadPreferenceBoolean(preferences, "nativeShowPoi", fallback: true);
        var filters = ReadPreferenceBooleanMap(preferences, "nativePoiFilters");

        mapPrototypeViewModel.SelectPoiSource(poiSource);
        mapPrototypeViewModel.SelectMapBase(mapBase);
        if (!string.IsNullOrWhiteSpace(mapId))
        {
            mapPrototypeViewModel.SelectMap(mapId);
        }

        if (filters.Count > 0)
        {
            mapPrototypeViewModel.SelectPoiFilters(filters);
        }

        mapPrototypeViewModel.WithPoiVisibility(showPoi);
        ApplyNativeMapRuntimePreferences(preferences);
    }

    private void ApplyNativeMapRuntimePreferences(JsonObject preferences)
    {
        NativeMapPrototype.SetAutoRefreshEnabled(ReadPreferenceBoolean(preferences, "nativeAutoRefresh", fallback: false));
        NativeMapPrototype.SetAutoMapEnabled(ReadPreferenceBoolean(preferences, "nativeAutoMap", fallback: false));
        NativeMapPrototype.SetPipEnabled(ReadPreferenceBoolean(preferences, "nativeMapPipEnabled", fallback: false));
        NativeMapPrototype.SetShowPoiEnabled(ReadPreferenceBoolean(preferences, "nativeShowPoi", fallback: true));
        NativeMapPrototype.SetRaidDetailsExpanded(ReadPreferenceBoolean(preferences, "nativeRaidDetailsExpanded", fallback: false));
        if (ReadPreferenceBoolean(preferences, "nativeMapPipEnabled", fallback: false))
        {
            EnsureNativeMapPipWindow();
            UpdateNativeMapPipSnapshot();
        }
        else
        {
            CloseNativeMapPipWindow(saveDisabled: false);
        }
    }

    private void OnNativeMapRefreshPositionRequested(object? sender, EventArgs e)
    {
        RefreshNativeMapPosition();
    }

    private async void OnNativeMapPickScreenshotDirectoryRequested(object? sender, EventArgs e)
    {
        await RunUiAsync(() => PickNativeDirectoryAsync("screenshots", "截图目录已选择并记住"));
    }

    private async void OnNativeMapPickLogDirectoryRequested(object? sender, EventArgs e)
    {
        await RunUiAsync(() => PickNativeDirectoryAsync("logs", "Log目录已选择并记住"));
    }

    private void OnNativeMapToggleWatchRequested(object? sender, EventArgs e)
    {
        if (nativeMapWatchTimer.IsRunning)
        {
            nativeMapWatchTimer.Stop();
            NativeMapPrototype.SetWatchEnabled(false);
            return;
        }

        nativeMapAutoRefreshTimer.Stop();
        NativeMapPrototype.SetAutoRefreshEnabled(false);
        nativeMapWatchTimer.Interval = TimeSpan.FromSeconds(NativeMapPrototype.PollingIntervalSeconds);
        nativeMapWatchTimer.Start();
        NativeMapPrototype.SetWatchEnabled(true);
        RefreshNativeMapPosition();
        _ = RefreshNativeRaidLogAsync();
    }

    private void OnNativeMapAutoRefreshChanged(object? sender, bool enabled)
    {
        SaveNativeMapPreference(preferences => preferences["nativeAutoRefresh"] = enabled);
        if (enabled && NativeMapPrototype.Visibility == Visibility.Visible)
        {
            nativeMapWatchTimer.Stop();
            NativeMapPrototype.SetWatchEnabled(false);
            nativeMapAutoRefreshTimer.Start();
            RefreshNativeMapPosition();
        }
        else
        {
            nativeMapAutoRefreshTimer.Stop();
        }
    }

    private void OnNativeMapAutoMapChanged(object? sender, bool enabled)
    {
        SaveNativeMapPreference(preferences => preferences["nativeAutoMap"] = enabled);
        NativeMapPrototype.ShowStatus(enabled ? "Auto Map 已开启" : "Auto Map 已关闭");
        _ = RefreshNativeRaidLogAsync();
    }

    private void OnNativeMapPipChanged(object? sender, bool enabled)
    {
        SaveNativeMapPreference(preferences => preferences["nativeMapPipEnabled"] = enabled);
        if (enabled)
        {
            EnsureNativeMapPipWindow();
            UpdateNativeMapPipSnapshot();
            NativeMapPrototype.ShowStatus("地图画中画已开启");
        }
        else
        {
            CloseNativeMapPipWindow(saveDisabled: true);
            NativeMapPrototype.ShowStatus("地图画中画已关闭");
        }
    }

    private void OnNativeMapShowPoiChanged(object? sender, bool showPoi)
    {
        SaveNativeMapPreference(preferences => preferences["nativeShowPoi"] = showPoi);
        var selectedMap = mapPrototypeViewModel.WithPoiVisibility(showPoi);
        NativeMapPrototype.LoadMap(selectedMap);
        NativeMapPrototype.SetPoiFilters(mapPrototypeViewModel.PoiFilters);
    }

    private void OnNativeMapStartupPageChanged(object? sender, bool nativeMap)
    {
        appConfigService.SaveStartupPage("native-map");
        NativeMapPrototype.SetStartupPagePreference(true);
        NativeMapPrototype.ShowStatus("启动默认页已固定为地图");
    }

    private void OnNativeMapAutoRefreshTick(DispatcherQueueTimer sender, object args)
    {
        if (NativeMapPrototype.Visibility == Visibility.Visible)
        {
            RefreshNativeMapPosition();
            _ = RefreshNativeRaidLogAsync();
        }
        else
        {
            StopNativeMapAutoRefresh();
        }
    }

    private void OnNativeMapWatchTick(DispatcherQueueTimer sender, object args)
    {
        if (NativeMapPrototype.Visibility == Visibility.Visible)
        {
            RefreshNativeMapPosition();
            _ = RefreshNativeRaidLogAsync();
        }
        else
        {
            nativeMapWatchTimer.Stop();
            NativeMapPrototype.SetWatchEnabled(false);
        }
    }

    private void OnNativeLanSyncTick(DispatcherQueueTimer sender, object args)
    {
        if (!lanPeerSyncService.IsRunning)
        {
            sender.Stop();
            RefreshNativeLanPeerUi();
            return;
        }

        _ = PublishNativeLanStateAsync();
        RefreshNativeLanPeerUi();
    }

    private void RefreshNativeMapPosition()
    {
        var result = mapPrototypeViewModel.RefreshLatestCoordinate(NativeMapPrototype.IsAutoMapEnabled);
        latestNativeCoordinate = result.Coordinate;
        latestNativePlayerPoint = result.PlayerPoint;
        NativeMapPrototype.SetAvailableMaps(
            mapPrototypeViewModel.AvailableMaps,
            mapPrototypeViewModel.CurrentMap.Id);
        NativeMapPrototype.ApplyProjectionResult(result);
        UpdateNativeMapPipSnapshot();
        _ = PublishNativeLanStateAsync();
        RefreshNativeLanPeerUi();
    }

    private async Task RefreshNativeRaidLogAsync()
    {
        if (isRefreshingRaidLog)
        {
            return;
        }

        isRefreshingRaidLog = true;
        try
        {
            var paths = appConfigService.ReadLocalPaths();
            paths.TryGetValue("game", out var logPath);
            var snapshot = await raidLogMonitorService.RefreshAsync(
                logPath,
                startupCancellation?.Token ?? CancellationToken.None);
            ApplyNativeRaidLogSnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            isRefreshingRaidLog = false;
        }
    }

    private void ApplyNativeRaidLogSnapshot(RaidLogSnapshot snapshot)
    {
        if (NativeMapPrototype.IsAutoMapEnabled && !string.IsNullOrWhiteSpace(snapshot.MapId))
        {
            var selectedMap = mapPrototypeViewModel.SelectMapFromLog(snapshot.MapId);
            if (selectedMap is not null)
            {
                NativeMapPrototype.SetAvailableMaps(
                    mapPrototypeViewModel.AvailableMaps,
                    selectedMap.Id);
                NativeMapPrototype.LoadMap(selectedMap);
                UpdateNativeMapPipSnapshot();
                RefreshNativeMapPosition();
                RefreshNativeLanPeerUi();
            }
        }

        NativeMapPrototype.ApplyRaidLogSnapshot(snapshot);
    }

    private void OnNativeMapSelectionRequested(object? sender, string mapId)
    {
        var selectedMap = mapPrototypeViewModel.SelectMap(mapId);
        if (selectedMap is not null)
        {
            SaveNativeMapPreference(preferences => preferences["nativeMapId"] = selectedMap.Id);
            NativeMapPrototype.LoadMap(selectedMap);
            UpdateNativeMapPipSnapshot();
            NativeMapPrototype.SetPoiFilters(mapPrototypeViewModel.PoiFilters);
            RefreshNativeLanPeerUi();
        }
    }

    private void StopNativeMapAutoRefresh()
    {
        nativeMapAutoRefreshTimer.Stop();
        nativeMapWatchTimer.Stop();
        NativeMapPrototype.SetAutoRefreshEnabled(false);
        NativeMapPrototype.SetWatchEnabled(false);
    }

    private void OnNativeMapPoiSourceSelectionRequested(object? sender, string poiSource)
    {
        var selectedMap = mapPrototypeViewModel.SelectPoiSource(poiSource);
        SaveNativeMapPreference(preferences => preferences["nativePoiSource"] = selectedMap.PoiSource);
        NativeMapPrototype.SetAvailableMaps(
            mapPrototypeViewModel.AvailableMaps,
            selectedMap.Id);
        NativeMapPrototype.LoadMap(selectedMap);
        UpdateNativeMapPipSnapshot();
        NativeMapPrototype.SetPoiFilters(mapPrototypeViewModel.PoiFilters);
        RefreshNativeLanPeerUi();
    }

    private void OnNativeMapBaseSelectionRequested(object? sender, string mapBase)
    {
        var selectedMap = mapPrototypeViewModel.SelectMapBase(mapBase);
        SaveNativeMapPreference(preferences => preferences["nativeMapBase"] = selectedMap.MapBase);
        NativeMapPrototype.SetAvailableMaps(
            mapPrototypeViewModel.AvailableMaps,
            selectedMap.Id);
        NativeMapPrototype.LoadMap(selectedMap);
        UpdateNativeMapPipSnapshot();
        NativeMapPrototype.SetPoiFilters(mapPrototypeViewModel.PoiFilters);
        RefreshNativeLanPeerUi();
    }

    private void OnNativeMapPoiFiltersChanged(object? sender, IReadOnlyDictionary<string, bool> filters)
    {
        SaveNativeMapPoiFilters(filters);
        var selectedMap = mapPrototypeViewModel.SelectPoiFilters(filters);
        NativeMapPrototype.LoadMap(selectedMap);
        UpdateNativeMapPipSnapshot();
        NativeMapPrototype.SetPoiFilters(mapPrototypeViewModel.PoiFilters);
        RefreshNativeLanPeerUi();
    }

    private void OnNativeMapRaidDetailsChanged(object? sender, bool expanded)
    {
        SaveNativeMapPreference(preferences => preferences["nativeRaidDetailsExpanded"] = expanded);
    }

    private void OnNativeMapLanSyncConfigChanged(object? sender, Models.NativeLanSyncRequest request)
    {
        SaveNativeLanSyncConfig(request, enabled: lanPeerSyncService.IsRunning);
    }

    private async void OnNativeMapLanSyncStartRequested(object? sender, Models.NativeLanSyncRequest request)
    {
        SaveNativeLanSyncConfig(request, enabled: true);
        try
        {
            if (string.Equals(request.Mode, "join", StringComparison.OrdinalIgnoreCase))
            {
                await lanPeerSyncService.StartJoinAsync(request, startupCancellation?.Token ?? CancellationToken.None);
            }
            else
            {
                await lanPeerSyncService.StartHostAsync(request, startupCancellation?.Token ?? CancellationToken.None);
            }

            nativeLanSyncTimer.Start();
            await PublishNativeLanStateAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NativeMapPrototype.ShowStatus($"LAN sync failed: {ex.Message}");
        }
        finally
        {
            RefreshNativeLanPeerUi();
        }
    }

    private async void OnNativeMapLanSyncStopRequested(object? sender, EventArgs e)
    {
        nativeLanSyncTimer.Stop();
        await lanPeerSyncService.StopAsync();
        SaveNativeLanSyncConfig(NativeMapPrototype.CurrentLanSyncRequest, enabled: false);
        RefreshNativeLanPeerUi();
    }

    private void SaveNativeLanSyncConfig(Models.NativeLanSyncRequest request, bool enabled)
    {
        var mode = string.Equals(request.Mode, "join", StringComparison.OrdinalIgnoreCase) ? "join" : "host";
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["enabled"] = enabled,
            ["mode"] = enabled ? mode : "off",
            ["syncMode"] = mode,
            ["displayName"] = request.DisplayName,
            ["color"] = request.Color,
            ["remoteEndpoint"] = request.RemoteEndpoint
        };
        var saved = appConfigService.SaveLanSyncConfig(payload);
        NativeMapPrototype.ApplyLanSyncConfig(saved);
    }

    private async Task PublishNativeLanStateAsync()
    {
        if (!lanPeerSyncService.IsRunning)
        {
            return;
        }

        await lanPeerSyncService.PublishAsync(
            NativeMapPrototype.CurrentLanSyncRequest,
            mapPrototypeViewModel.CurrentMap,
            latestNativeCoordinate);
    }

    private void RefreshNativeLanPeerUi()
    {
        NativeMapPrototype.ApplyLanSyncState(lanPeerSyncService.BuildUiState(
            mapPrototypeViewModel.CurrentMap,
            latestNativeCoordinate is not null));
        NativeMapPrototype.ApplyPeerMarkers(lanPeerSyncService.BuildPeerMarkers(mapPrototypeViewModel.CurrentMap));
    }

    private void RefreshScreenFilterUi()
    {
        try
        {
            NativeMapPrototype.SetScreenFilterDisplays(screenGammaService.GetDisplays());
        }
        catch (Exception ex)
        {
            NativeMapPrototype.ShowScreenFilterStatus($"屏幕滤镜: 读取屏幕失败: {ex.Message}");
        }

        NativeMapPrototype.ApplyScreenFilterConfig(appConfigService.ReadScreenFilterConfig());
    }

    private void OnScreenFilterConfigChanged(object? sender, ScreenFilterRequest request)
    {
        SaveScreenFilterConfig(request);
    }

    private void OnScreenFilterApplyRequested(object? sender, ScreenFilterRequest request)
    {
        var saved = SaveScreenFilterConfig(request);
        NativeMapPrototype.ApplyScreenFilterConfig(saved);

        try
        {
            var result = screenGammaService.Apply(request.ToPreset(), request.DisplayName);
            NativeMapPrototype.ShowScreenFilterStatus(result.Message);
        }
        catch (Exception ex)
        {
            NativeMapPrototype.ShowScreenFilterStatus($"屏幕滤镜失败: {ex.Message}");
        }
    }

    private void OnScreenFilterResetRequested(object? sender, ScreenFilterRequest request)
    {
        var resetRequest = request with
        {
            PresetId = "default",
            Gamma = 1.0,
            Brightness = 0,
            Contrast = 0,
            Red = 128,
            Green = 128,
            Blue = 128
        };
        var saved = SaveScreenFilterConfig(resetRequest);
        NativeMapPrototype.ApplyScreenFilterConfig(saved);

        try
        {
            var result = screenGammaService.Reset(request.DisplayName);
            NativeMapPrototype.ShowScreenFilterStatus(result.Message);
        }
        catch (Exception ex)
        {
            NativeMapPrototype.ShowScreenFilterStatus($"恢复默认失败: {ex.Message}");
        }
    }

    private JsonObject SaveScreenFilterConfig(ScreenFilterRequest request)
    {
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["presetId"] = request.PresetId,
            ["displayName"] = request.DisplayName,
            ["gamma"] = request.Gamma,
            ["brightness"] = request.Brightness,
            ["contrast"] = request.Contrast,
            ["red"] = request.Red,
            ["green"] = request.Green,
            ["blue"] = request.Blue
        };

        return appConfigService.SaveScreenFilterConfig(payload);
    }

    private void EnsureNativeMapPipWindow()
    {
        if (nativeMapPipWindow is not null)
        {
            return;
        }

        var preferences = ReadNativeMapPipPreferences(appConfigService.ReadUiPreferences());
        nativeMapPipWindow = new NativeMapPipWindow(preferences with { IsEnabled = true }, SaveNativeMapPipPreferences);
        nativeMapPipWindow.ClosedByUser += OnNativeMapPipClosedByUser;
        nativeMapPipWindow.Activate();
        NativeMapPrototype.SetPipEnabled(true);
    }

    private void UpdateNativeMapPipSnapshot()
    {
        if (nativeMapPipWindow is null)
        {
            return;
        }

        nativeMapPipWindow.ApplySnapshot(new NativeMapPipSnapshot(
            mapPrototypeViewModel.CurrentMap,
            latestNativePlayerPoint));
    }

    private void CloseNativeMapPipWindow(bool saveDisabled)
    {
        var window = nativeMapPipWindow;
        if (window is null)
        {
            if (saveDisabled)
            {
                SaveNativeMapPreference(preferences => preferences["nativeMapPipEnabled"] = false);
            }

            NativeMapPrototype.SetPipEnabled(false);
            return;
        }

        nativeMapPipWindow = null;
        window.ClosedByUser -= OnNativeMapPipClosedByUser;
        if (saveDisabled)
        {
            SaveNativeMapPreference(preferences => preferences["nativeMapPipEnabled"] = false);
        }

        window.Close();
        NativeMapPrototype.SetPipEnabled(false);
    }

    private void OnNativeMapPipClosedByUser(object? sender, EventArgs e)
    {
        nativeMapPipWindow = null;
        SaveNativeMapPreference(preferences => preferences["nativeMapPipEnabled"] = false);
        NativeMapPrototype.SetPipEnabled(false);
    }

    private void SaveNativeMapPipPreferences(NativeMapPipPreferences pipPreferences)
    {
        SaveNativeMapPreference(preferences =>
        {
            preferences["nativeMapPipEnabled"] = pipPreferences.IsEnabled;
            preferences["nativeMapPipOpacity"] = pipPreferences.Opacity;
            preferences["nativeMapPipZoom"] = pipPreferences.Zoom;
            preferences["nativeMapPipLeft"] = pipPreferences.Left;
            preferences["nativeMapPipTop"] = pipPreferences.Top;
            preferences["nativeMapPipWidth"] = pipPreferences.Width;
            preferences["nativeMapPipHeight"] = pipPreferences.Height;
        });
    }

    private void OnNativeMapCheckCacheRequested(object? sender, EventArgs e)
    {
        var result = mapImageCacheService.CheckPrototypeMapImages(mapPrototypeViewModel.AllMaps);
        if (result.HasMissingOrExpired)
        {
            RefreshNativeMapListFromMetadata(allowMapReload: true);
        }

        NativeMapPrototype.ShowStatus(result.StatusMessage);
    }

    private void RefreshNativeMapListFromMetadata(bool allowMapReload)
    {
        var previousMapId = mapPrototypeViewModel.CurrentMap.Id;
        var previousImagePath = mapPrototypeViewModel.CurrentMap.ImagePath;
        var refreshed = mapPrototypeViewModel.ReloadAvailableMaps();
        NativeMapPrototype.SetAvailableMaps(refreshed.Maps, refreshed.CurrentMap.Id);

        if (!allowMapReload || !nativeMapLoaded)
        {
            return;
        }

        var imageChanged = !string.Equals(previousImagePath, refreshed.CurrentMap.ImagePath, StringComparison.OrdinalIgnoreCase);
        var hadNoImage = string.IsNullOrWhiteSpace(previousImagePath) || !File.Exists(previousImagePath);
        if (refreshed.CurrentMap.Id != previousMapId || hadNoImage || imageChanged)
        {
            NativeMapPrototype.LoadMap(refreshed.CurrentMap);
            UpdateNativeMapPipSnapshot();
        }

        raidLogMonitorService.ReloadMapLookup();
    }

    private async Task PickNativeDirectoryAsync(string purpose, string successMessage)
    {
        if (folderPickerService is null)
        {
            NativeMapPrototype.ShowStatus("目录选择服务尚未初始化");
            return;
        }

        try
        {
            var path = await folderPickerService.PickFolderAsync(purpose);
            if (path is null)
            {
                NativeMapPrototype.ShowStatus("已取消目录选择");
                return;
            }

            RefreshNativeMapLocalState();
            NativeMapPrototype.ShowStatus(successMessage);
            if (purpose is "screenshots")
            {
                RefreshNativeMapPosition();
            }
            else if (purpose is "logs")
            {
                await RefreshNativeRaidLogAsync();
            }
        }
        catch (Exception ex)
        {
            NativeMapPrototype.ShowStatus($"目录选择失败: {ex.Message}");
        }
    }

    private void RefreshNativeMapLocalState()
    {
        NativeMapPrototype.SetLocalPathLabels(appConfigService.ReadLocalPaths());
        NativeMapPrototype.ApplyLanSyncConfig(appConfigService.ReadLanSyncConfig());
    }

    private void AutoDetectNativeLocalPaths()
    {
        var paths = appConfigService.ReadLocalPaths();
        var changed = false;

        if (!paths.TryGetValue("screenshot", out var screenshotPath) ||
            !pathAutoDetectService.IsUsableScreenshotDirectory(screenshotPath))
        {
            var detectedScreenshotPath = pathAutoDetectService.DetectScreenshotDirectory();
            if (!string.IsNullOrWhiteSpace(detectedScreenshotPath))
            {
                appConfigService.SaveLocalPath("screenshot", detectedScreenshotPath);
                changed = true;
            }
        }

        if (!paths.TryGetValue("game", out var logPath) ||
            !pathAutoDetectService.IsUsableLogDirectory(logPath))
        {
            var detectedLogPath = pathAutoDetectService.DetectLogDirectory();
            if (!string.IsNullOrWhiteSpace(detectedLogPath))
            {
                appConfigService.SaveLocalPath("game", detectedLogPath);
                changed = true;
            }
        }

        if (changed)
        {
            NativeMapPrototype.ShowStatus("已自动选择可用的截图或 Log 目录");
        }
    }

    private void SaveNativeMapPreference(Action<JsonObject> update)
    {
        var preferences = appConfigService.ReadUiPreferences();
        update(preferences);
        appConfigService.SaveUiPreferences(preferences);
    }

    private void SaveNativeMapPoiFilters(IReadOnlyDictionary<string, bool> filters)
    {
        SaveNativeMapPreference(preferences =>
        {
            var payload = new JsonObject();
            foreach (var item in filters)
            {
                payload[item.Key] = item.Value;
            }

            preferences["nativePoiFilters"] = payload;
        });
    }

    private static string ReadPreferenceString(JsonObject payload, string propertyName, string fallback = "")
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

    private static bool ReadPreferenceBoolean(JsonObject payload, string propertyName, bool fallback)
    {
        try
        {
            return payload[propertyName]?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static IReadOnlyDictionary<string, bool> ReadPreferenceBooleanMap(JsonObject payload, string propertyName)
    {
        var node = payload[propertyName] as JsonObject;
        if (node is null)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in node)
        {
            try
            {
                result[item.Key] = item.Value?.GetValue<bool>() ?? true;
            }
            catch
            {
                result[item.Key] = true;
            }
        }

        return result;
    }

    private static NativeMapPipPreferences ReadNativeMapPipPreferences(JsonObject payload)
    {
        return new NativeMapPipPreferences(
            ReadPreferenceBoolean(payload, "nativeMapPipEnabled", fallback: false),
            ReadPreferenceDouble(payload, "nativeMapPipOpacity", 0.35, 1.0, 0.92),
            ReadPreferenceDouble(payload, "nativeMapPipZoom", 1.0, 5.0, 1.0),
            ReadPreferenceInt(payload, "nativeMapPipLeft", 0, 20000, 80),
            ReadPreferenceInt(payload, "nativeMapPipTop", 0, 20000, 80),
            ReadPreferenceInt(payload, "nativeMapPipWidth", 220, 1200, 420),
            ReadPreferenceInt(payload, "nativeMapPipHeight", 220, 1200, 320));
    }

    private static int ReadPreferenceInt(JsonObject payload, string propertyName, int min, int max, int fallback)
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

    private static double ReadPreferenceDouble(JsonObject payload, string propertyName, double min, double max, double fallback)
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
}
