using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocatorDesktop.Controls;
using TarkovMapLocatorDesktop.Models;
using TarkovMapLocatorDesktop.Services;
using TarkovMapLocatorDesktop.Utilities;
using TarkovMapLocatorDesktop.ViewModels;
using TarkovMapLocatorDesktop.Windows;

namespace TarkovMapLocatorDesktop;

public partial class MainWindow : Window, IFeatureHost
{
    private const int MapLayerDecodePixelWidthLimit = 3200;
    private const int MapLayerImageCacheCapacity = 2;
    private const int MapBaseStyleImageCacheCapacity = 2;
    private static readonly TimeSpan RaidStartCoordinateTolerance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MapEvidenceCoordinateTolerance = TimeSpan.FromSeconds(10);

    private enum WorkspacePage { Map, Market, InGamePrice, TaskItems, Memo, TaskTracking, ScreenFilter, TeamSync, MobileMap, Utilities, Settings }
    private enum AirdropLocatorStage { Inactive, WaitingForFirst, WaitingForSecond, Solved }
    private sealed record FeaturePageRegistration(
        string Route,
        WorkspacePage Page,
        FrameworkElement PageElement,
        Button RailButton,
        ContentControl Host,
        bool UsesMarketData = false);
    private sealed record RenderedMarker(FrameworkElement Element, MapMarker Marker);
    private sealed record MarkerLabelLayout(Canvas Host, Border Label, Line Leader, double DotSize, double BaseTop);
    private static readonly Geometry DirectionArrowGeometry = Geometry.Parse("M8,0 L16,8 L11.5,8 L11.5,16 L4.5,16 L4.5,8 L0,8 Z");
    private static readonly Geometry LockMarkerGeometry = Geometry.Parse("M5,7 V5 A3,3 0 0 1 11,5 V7 M3.5,7 H12.5 V14 H3.5 Z M8,9.5 V12");
#if DEBUG
    private static readonly MapMarker PlayerMarkerPreview = new()
    {
        Type = "player-preview",
        Label = "玩家位置（测试）",
        X = .52,
        Y = .52,
        ShowLabel = true,
        HeadingDegrees = 35
    };
#endif

    private readonly MainViewModel _viewModel = new();
    private readonly MapLayerCatalogLoadResult _mapLayerCatalog = MapLayerCatalogService.Load();
    private readonly Dictionary<string, string> _selectedMapLayerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _selectedMapStyles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<MapLayerDefinition>> _satelliteMapLayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _mapLayerImageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _mapLayerImageLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _mapLayerImageLru = [];
    private readonly Dictionary<string, ImageSource> _mapBaseStyleImageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _mapBaseStyleImageLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _mapBaseStyleImageLru = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private readonly LocalRaidMonitorService _raidMonitor = new();
    private TaskStatusLogMonitorService? _taskStatusMonitor;
    private readonly GlobalCtrlTapService _ctrlTapService = new();
    private readonly BtrPredictionAdapter _btrPrediction = new();
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TaskPointLoadResult _taskPointLoad = TaskPointService.Load();
    private readonly HashSet<string> _selectedTaskKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedTaskKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taskTrackingLinkedSelectionKeys = new(StringComparer.Ordinal);
    private MarketCatalog _marketCatalog = new([], [], null, "", "行情缓存未加载");
    private DesktopPreferences _desktopPreferences = DesktopPreferences.Empty;
    private MiniMapWindow? _miniMapWindow;
    private Point _panStart;
    private bool _isPanning;
    private bool _isLibraryCollapsed;
    private bool _isInspectorCollapsed;
    private bool _isRaidStatusCollapsed;
    private bool _isRaidStatusAnimationRunning;
    private double _mapInspectorPaneWidth = 310;
    private double? _mapInspectorPaneHeight;
    private bool _isPaneTransitionRunning;
    private int _paneTransitionVersion;
    private bool _isPointDisplayPanelExpanded;
    private int _pointDisplayTransitionVersion;
    private bool _initialPlacementApplied;
    private bool _requiresInitialPathSetup;
    private bool _isListening;
    private bool _isAutoRefreshEnabled = true;
    private bool _isRaidSnapshotRefreshRunning;
    private bool _isWindowClosing;
    private bool _allowMainWindowClose;
    private bool _closeCleanupCompleted;
    private Task? _closeCleanupTask;
    private bool _isUpdatingCloseBehaviorUi;
    private bool _autoMapLayerEnabled = true;
    private bool _suppressMapLayerSelection;
    private bool _suppressMapStyleSelection;
    private int _mapBaseStyleLoadVersion;
    private string _activeMapStyle = "2d";
    private string? _activeMapStyleMapId;
    private MapLayerDefinition? _activeMapLayer;
    private string? _mapLayerMapId;
    private string _mapLayerStyle = "2d";
    private int _mapLayerLoadVersion;
    private LocalRaidSnapshot? _lastRaidSnapshot;
    private string? _lastAppliedRaidMapSignature;
    private bool _isApplyingAutomaticMapSelection;
    private bool _runtimeLogViewDirty = true;
    private bool _updatingRuntimeLogFilters;
    private bool _runtimeLogAutoScroll = true;
    private string _runtimeLogFilteredText = string.Empty;
    private int _runtimeLogFilteredEntryCount;
    private WorkspacePage _activePage = WorkspacePage.Map;
    private readonly Stack<WorkspacePage> _backHistory = [];
    private readonly Stack<WorkspacePage> _forwardHistory = [];
    private bool _isNavigatingHistory;
    private string _marketMode = "pvp";
    private bool _marketAutomaticRefreshRunning;
    private bool _marketStartupRefreshStarted;
    private bool _marketStartupInitializationStarted;
    private bool _marketStartupInitializationRunning;
    private bool _marketMemoryReleaseRequested;
    private readonly List<RenderedMarker> _staticRenderedMarkers = [];
    private readonly List<RenderedMarker> _dynamicRenderedMarkers = [];
    private readonly DispatcherTimer _runtimeLogRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private int _runtimeLogRefreshDispatchQueued;
    private string? _lastRaidReadTraceSignature;
    private DateTimeOffset _lastRaidReadTraceAt;
    private DateTimeOffset _lastRaidSkipTraceAt;
    private readonly RowDefinition _compactLibraryRow = new() { Height = new GridLength(220) };
    private readonly RowDefinition _compactContentRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    private readonly RowDefinition _compactInspectorRow = new() { Height = new GridLength(340) };
    private bool _isCompactLayout;
    private bool _compactLayoutInitialized;
    private bool _layoutRefreshQueued;
    private bool _markerRepositionQueued;
    private bool _staticMarkerVisualsInitialized;
    private bool _dynamicMarkerVisualsInitialized;
    private string? _staticMarkerVisualSignature;
    private string? _dynamicMarkerVisualSignature;
    private double _zoom = 1;
    private const double MinimumMapZoom = .5;
    private const double MaximumMapZoom = 4;
    private int _toastAnimationVersion;
    private int _mapTransitionVersion;
    private AirdropLocatorStage _airdropLocatorStage;
    private AirdropBearingSample? _airdropFirstBearing;
    private AirdropBearingSample? _airdropSecondBearing;
    private AirdropEstimate? _airdropEstimate;
    private string? _airdropIssue;
    private string? _airdropLastProcessedFileName;
    private DateTimeOffset _airdropActivatedAt;
    private bool _ctrlTapListenerActive;
    private FeatureModuleCatalog _featureModules = FeatureModuleCatalog.Empty;
    private IReadOnlyList<FeaturePageRegistration> _featurePages = [];
    private readonly List<(string DisplayName, IAsyncDisposable Lifecycle)> _featureViewLifecycles = [];
    private ITaskTrackingFeature? _taskTrackingFeature;
    private ITeamSyncFeature? _teamSyncFeature;
    private IMobileMapFeature? _mobileMapFeature;

    public event EventHandler? MarketDataChanged;

    public MainWindow() : this(null)
    {
    }

    internal MainWindow(FeatureModuleCatalog? featureModules)
    {
        InitializeComponent();
        InitializeFeaturePageRegistry();
        InitializeFeatureModules(featureModules);
        InitializeRuntimeLogControls();
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        _ctrlTapService.CtrlTapped += CtrlTapService_CtrlTapped;
        RuntimeLogService.Changed += RuntimeLogService_Changed;
        RuntimeLogService.Info(
            "应用",
            "塔科夫工具箱启动",
            $"版本: {typeof(MainWindow).Assembly.GetName().Version}\n" +
            $"运行时: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
            $"系统: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"进程架构: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
            $"程序目录: {AppContext.BaseDirectory}\n" +
            $"日志文件: {RuntimeLogService.CurrentLogFilePath}");
        _runtimeLogRefreshTimer.Tick += (_, _) =>
        {
            _runtimeLogRefreshTimer.Stop();
            if (_activePage == WorkspacePage.Settings) RefreshRuntimeLogView();
        };
        DataContext = _viewModel;
        _viewModel.MapImageLoaded += ViewModel_MapImageLoaded;
        MapList.SelectedItem = _viewModel.SelectedMap;
        _desktopPreferences = DesktopPreferencesService.Load();
        _mapInspectorPaneWidth = _desktopPreferences.MapInspectorPaneWidth ?? 310;
        _mapInspectorPaneHeight = _desktopPreferences.MapInspectorPaneHeight;
        if (_taskTrackingFeature is not null)
        {
            _taskTrackingFeature.AutomaticTaskRecognitionEnabled = _desktopPreferences.AutoRecognizeTaskStatuses;
            _taskTrackingFeature.AutoCompletePrerequisitesEnabled = _desktopPreferences.AutoCompleteTaskPrerequisites;
        }
        _autoMapLayerEnabled = _desktopPreferences.AutoSwitchMapLayer;
        foreach (var pair in _desktopPreferences.SelectedMapLayers ?? [])
            _selectedMapLayerIds[pair.Key] = pair.Value;
        foreach (var pair in _desktopPreferences.SelectedMapStyles ?? [])
            _selectedMapStyles[pair.Key] = pair.Value;
        RefreshMapStyleControlsForSelectedMap("恢复底图配置");
        RefreshMapLayerControlsForSelectedMap("恢复楼层配置");
        RuntimeLogService.Info(
            "配置",
            "已加载桌面版配置",
            $"截图目录: {_desktopPreferences.ScreenshotDirectory ?? "未配置"}\n" +
            $"游戏日志目录: {_desktopPreferences.GameLogDirectory ?? "未配置"}\n" +
            $"已选任务: {_desktopPreferences.SelectedTaskKeys.Length}\n" +
            $"已完成任务: {_desktopPreferences.CompletedTaskKeys.Length}\n" +
            $"自动切层: {(_autoMapLayerEnabled ? "开启" : "关闭")}\n" +
            $"可用楼层图: {_mapLayerCatalog.AvailableLayerCount}\n" +
            "行情缓存: 首次打开相关功能时加载");
        _selectedTaskKeys.UnionWith(_desktopPreferences.SelectedTaskKeys);
        _completedTaskKeys.UnionWith(_desktopPreferences.CompletedTaskKeys);
        _selectedTaskKeys.ExceptWith(_completedTaskKeys);
        _requiresInitialPathSetup = string.IsNullOrWhiteSpace(_desktopPreferences.ScreenshotDirectory) || string.IsNullOrWhiteSpace(_desktopPreferences.GameLogDirectory);
        _toastTimer.Tick += (_, _) =>
        {
            HideToast();
        };
        _autoRefreshTimer.Tick += async (_, _) =>
        {
            if (_isListening && _isAutoRefreshEnabled) await RefreshLocalRaidDataAsync();
            else _autoRefreshTimer.Stop();
        };
        SizeChanged += Window_SizeChanged;
        Loaded += (_, _) =>
        {
            if (_isWindowClosing || !IsVisible) return;
            RuntimeLogService.Info("界面", "主窗口已加载", $"窗口尺寸: {ActualWidth:0} × {ActualHeight:0}\n显示缩放动画: {(MotionEnabled ? "启用" : "关闭")}");
            RefreshMapLayerControlsForSelectedMap("界面加载");
            UpdateMarkers();
            UpdateTaskConfiguration();
            UpdateSettingsPage();
            ShowWorkspacePage(_requiresInitialPathSetup ? WorkspacePage.Settings : WorkspacePage.Map);
            UpdateListeningControls();
            InitializeCtrlTapShortcut();
            UpdateAirdropLocatorUi();
        };
        Closing += MainWindow_Closing;
    }

    internal Task PrepareForCloseAsync() => _closeCleanupTask ??= PrepareForCloseCoreAsync();

    private async Task PrepareForCloseCoreAsync()
    {
        try
        {
            _isWindowClosing = true;
            _lifetimeCts.Cancel();
            _ctrlTapService.CtrlTapped -= CtrlTapService_CtrlTapped;
            _ctrlTapService.Dispose();
            RuntimeLogService.Info("应用", "主窗口关闭", $"最后地图: {_viewModel.CurrentMapName}\n监听状态: {(_isListening ? "监听中" : "未监听")}");
            RuntimeLogService.Changed -= RuntimeLogService_Changed;
            _viewModel.MapImageLoaded -= ViewModel_MapImageLoaded;
            _autoRefreshTimer.Stop();
            _runtimeLogRefreshTimer.Stop();
            _mapLayerLoadVersion++;
            _raidMonitor.Dispose();
        }
        finally
        {
            await DisposeFeatureViewsAsync();
            _miniMapWindow?.Close();
            _miniMapWindow = null;
            _closeCleanupCompleted = true;
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCleanupCompleted) return;
        if (_isWindowClosing)
        {
            e.Cancel = true;
            return;
        }

        ConfirmMainWindowClose(sender, e);
        if (e.Cancel) return;

        // Keep the dispatcher alive until every optional view finishes cleanup.
        // Closing again must be queued even when all cleanup completes inline.
        e.Cancel = true;
        IsEnabled = false;
        try { await PrepareForCloseAsync(); }
        catch (Exception exception)
        {
            RuntimeLogService.Error("应用", "关闭时清理资源失败", exception);
        }
        finally
        {
            _closeCleanupCompleted = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void ConfirmMainWindowClose(object? sender, CancelEventArgs e)
    {
        // Startup validation creates and disposes a non-visible MainWindow.
        // A modal prompt cannot have a non-visible owner, so only an actual
        // user-facing title-bar close is eligible for this confirmation.
        if (_allowMainWindowClose || !IsVisible) return;

        if (_desktopPreferences.SuppressClosePrompt)
        {
            if (_desktopPreferences.MinimizeWhenClosing)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
            }
            return;
        }

        var prompt = new MainWindowClosePromptWindow { Owner = this };
        if (prompt.ShowDialog() != true || prompt.SelectedAction == MainWindowCloseAction.None)
        {
            e.Cancel = true;
            return;
        }

        if (prompt.DontAskAgain)
        {
            var minimizeWhenClosing = prompt.SelectedAction == MainWindowCloseAction.Minimize;
            if (DesktopPreferencesService.SaveCloseBehavior(true, minimizeWhenClosing))
            {
                _desktopPreferences = _desktopPreferences with
                {
                    SuppressClosePrompt = true,
                    MinimizeWhenClosing = minimizeWhenClosing
                };
            }
            else
            {
                // The choice still applies to this close operation, but do not
                // pretend it will survive restart when the profile is read-only.
                RuntimeLogService.Warning("配置", "保存关闭行为失败", "本次关闭会按所选操作执行；下次启动仍会询问。");
                ShowToast("关闭行为未能保存；下次启动仍会询问");
            }
        }

        if (prompt.SelectedAction == MainWindowCloseAction.Minimize)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        _allowMainWindowClose = true;
    }

    private void InitializeCtrlTapShortcut()
    {
        _ctrlTapListenerActive = _ctrlTapService.Start();
        AirdropShortcutText.Text = _ctrlTapListenerActive ? "Ctrl" : "点击启用";
        AirdropLocatorButton.ToolTip = _ctrlTapListenerActive
            ? "地图页单独轻按 Ctrl：开启或关闭空投定位；其他页面和 Ctrl 组合键不会触发"
            : "全局 Ctrl 监听不可用，请点击按钮开启空投定位";
        RuntimeLogService.Info(
            "空投定位",
            _ctrlTapListenerActive ? "全局 Ctrl 单击监听已启用" : "全局 Ctrl 单击监听启用失败",
            _ctrlTapListenerActive
                ? "地图页单独轻按 Ctrl 可开启或关闭；其他页面及 Ctrl 组合操作不会触发"
                : $"已保留界面按钮入口，Win32 错误码: {_ctrlTapService.LastError}");
    }

    private void CtrlTapService_CtrlTapped()
    {
        if (_isWindowClosing || _activePage != WorkspacePage.Map) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(CtrlTapService_CtrlTapped));
            return;
        }

        _ = ToggleAirdropLocalizationAsync();
    }

    private void ViewModel_MapImageLoaded(MapDefinition map)
    {
        if (!ReferenceEquals(map, _viewModel.SelectedMap) || _isWindowClosing) return;
        QueueMarkerReposition();
        UpdateMiniMap(GetVisibleMarkers());
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_initialPlacementApplied) return;

        _initialPlacementApplied = true;
        var workArea = SystemParameters.WorkArea;
        var portraitScreen = workArea.Height >= workArea.Width * 1.2;
        Width = portraitScreen
            ? Math.Clamp(workArea.Width - 32, MinWidth, Math.Min(1080, workArea.Width - 32))
            : Math.Min(Width, workArea.Width - 32);
        Height = portraitScreen
            ? Math.Min(workArea.Height - 32, 1600)
            : Math.Min(Height, workArea.Height - 32);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
        ApplyResponsiveLayout(force: true);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutRefreshQueued) return;
        _layoutRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _layoutRefreshQueued = false;
            ApplyResponsiveLayout();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyResponsiveLayout(bool force = false)
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_isPaneTransitionRunning && !force) return;
        var compact = ActualWidth <= 900 ||
                      ActualWidth <= 1240 && ActualHeight >= ActualWidth * 1.08;
        if (!force && compact == _isCompactLayout)
        {
            if (compact) UpdateCompactPaneSizes();
            else UpdateLandscapePaneSizes();
            return;
        }

        var enteringCompactLayout = compact && !_isCompactLayout;
        _isCompactLayout = compact;
        if (enteringCompactLayout && !_compactLayoutInitialized)
        {
            _compactLayoutInitialized = true;
            _isLibraryCollapsed = true;
            _isInspectorCollapsed = true;
        }

        RootLayout.ColumnDefinitions.Clear();
        RootLayout.RowDefinitions.Clear();
        if (compact)
        {
            RailColumn.Width = new GridLength(72);
            ContentColumn.Width = new GridLength(1, GridUnitType.Star);
            RootLayout.ColumnDefinitions.Add(RailColumn);
            RootLayout.ColumnDefinitions.Add(ContentColumn);
            RootLayout.RowDefinitions.Add(HeaderRow);
            RootLayout.RowDefinitions.Add(_compactLibraryRow);
            RootLayout.RowDefinitions.Add(_compactContentRow);
            RootLayout.RowDefinitions.Add(_compactInspectorRow);
            var showMapSidePanes = _activePage == WorkspacePage.Map;
            UpdateCompactPaneSizes(showMapSidePanes);

            SetRootPlacement(RailPane, 0, 0, 4);
            SetRootPlacement(TopBar, 0, 1);
            SetRootPlacement(LibraryPaneHost, 1, 1);
            SetRootPlacement(MapPage, 2, 1);
            SetRootPlacement(MarketPage, 2, 1);
            SetRootPlacement(InGamePricePage, 2, 1);
            SetRootPlacement(TaskItemsPage, 2, 1);
            SetRootPlacement(MemoPage, 2, 1);
            SetRootPlacement(TaskTrackingPage, 2, 1);
            SetRootPlacement(ScreenFilterPage, 2, 1);
            SetRootPlacement(TeamSyncPage, 2, 1);
            SetRootPlacement(MobileMapPage, 2, 1);
            SetRootPlacement(UtilitiesPage, 2, 1);
            SetRootPlacement(SettingsPage, 2, 1);
            SetRootPlacement(InspectorPaneHost, 3, 1);
            SetRootPlacement(MapTaskWidthSplitter, 2, 1);
            SetRootPlacement(MapTaskHeightSplitter, 2, 1);
            SetRootPlacement(LibraryPaneToggleButton, 2, 1);
            SetRootPlacement(InspectorPaneToggleButton, 2, 1);
            SetRootPlacement(ToastBorder, 2, 1);
            LibraryPane.BorderThickness = new Thickness(0, 0, 0, 1);
            InspectorPane.BorderThickness = new Thickness(0, 1, 0, 0);
            LibraryPane.Padding = new Thickness(16, 14, 14, 12);
            InspectorPane.Padding = new Thickness(16, 14, 16, 12);
            LibraryPane.Width = double.NaN;
            LibraryPane.Height = GetCompactLibraryHeight();
            LibraryPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            LibraryPane.VerticalAlignment = VerticalAlignment.Top;
            InspectorPane.Width = double.NaN;
            InspectorPane.Height = GetCompactInspectorHeight();
            TaskConfigPanel.MinHeight = 220;
            InspectorPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorPane.VerticalAlignment = VerticalAlignment.Bottom;
        }
        else
        {
            RailColumn.Width = new GridLength(72);
            var showMapSidePanes = _activePage == WorkspacePage.Map;
            LibraryColumn.Width = new GridLength(!showMapSidePanes || _isLibraryCollapsed ? 0 : 270);
            ContentColumn.Width = new GridLength(1, GridUnitType.Star);
            var inspectorWidth = GetLandscapeInspectorWidth();
            InspectorColumn.Width = new GridLength(!showMapSidePanes || _isInspectorCollapsed ? 0 : inspectorWidth);
            RootLayout.ColumnDefinitions.Add(RailColumn);
            RootLayout.ColumnDefinitions.Add(LibraryColumn);
            RootLayout.ColumnDefinitions.Add(ContentColumn);
            RootLayout.ColumnDefinitions.Add(InspectorColumn);
            RootLayout.RowDefinitions.Add(HeaderRow);
            RootLayout.RowDefinitions.Add(MainRow);

            SetRootPlacement(RailPane, 0, 0, 2);
            SetRootPlacement(TopBar, 0, 1, 1, 3);
            SetRootPlacement(LibraryPaneHost, 1, 1);
            SetRootPlacement(MapPage, 1, 2);
            SetRootPlacement(MarketPage, 1, 2);
            SetRootPlacement(InGamePricePage, 1, 2);
            SetRootPlacement(TaskItemsPage, 1, 2);
            SetRootPlacement(MemoPage, 1, 2);
            SetRootPlacement(TaskTrackingPage, 1, 2);
            SetRootPlacement(ScreenFilterPage, 1, 2);
            SetRootPlacement(TeamSyncPage, 1, 2);
            SetRootPlacement(MobileMapPage, 1, 2);
            SetRootPlacement(UtilitiesPage, 1, 2);
            SetRootPlacement(SettingsPage, 1, 2);
            SetRootPlacement(InspectorPaneHost, 1, 3);
            SetRootPlacement(MapTaskWidthSplitter, 1, 2);
            SetRootPlacement(MapTaskHeightSplitter, 1, 2);
            SetRootPlacement(LibraryPaneToggleButton, 1, 2);
            SetRootPlacement(InspectorPaneToggleButton, 1, 2);
            SetRootPlacement(ToastBorder, 1, 2);
            LibraryPane.BorderThickness = new Thickness(0, 0, 1, 0);
            InspectorPane.BorderThickness = new Thickness(1, 0, 0, 0);
            LibraryPane.Padding = new Thickness(16, 22, 14, 15);
            InspectorPane.Padding = new Thickness(16, 22, 16, 15);
            LibraryPane.Width = 270;
            LibraryPane.Height = double.NaN;
            LibraryPane.HorizontalAlignment = HorizontalAlignment.Left;
            LibraryPane.VerticalAlignment = VerticalAlignment.Stretch;
            InspectorPane.Width = inspectorWidth;
            InspectorPane.Height = double.NaN;
            TaskConfigPanel.MinHeight = 0;
            InspectorPane.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorPane.VerticalAlignment = VerticalAlignment.Stretch;
        }

        var shouldShowMapSidePanes = _activePage == WorkspacePage.Map;
        LibraryPaneHost.Visibility = shouldShowMapSidePanes && !_isLibraryCollapsed ? Visibility.Visible : Visibility.Collapsed;
        InspectorPaneHost.Visibility = shouldShowMapSidePanes && !_isInspectorCollapsed ? Visibility.Visible : Visibility.Collapsed;
        MapTaskWidthSplitter.Visibility = shouldShowMapSidePanes && !_isInspectorCollapsed && !_isCompactLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
        MapTaskHeightSplitter.Visibility = shouldShowMapSidePanes && !_isInspectorCollapsed && _isCompactLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePaneToggleControls();
        QueueMarkerReposition();
    }

    private static void SetRootPlacement(UIElement element, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private void LibraryPaneToggleButton_Click(object sender, RoutedEventArgs e) => ToggleSidePane(isLibrary: true);
    private void InspectorPaneToggleButton_Click(object sender, RoutedEventArgs e) => ToggleSidePane(isLibrary: false);

    private void ToggleSidePane(bool isLibrary)
    {
        if (_activePage != WorkspacePage.Map || _isPaneTransitionRunning) return;
        var isCollapsed = isLibrary ? _isLibraryCollapsed : _isInspectorCollapsed;
        if (_isCompactLayout)
        {
            if (!MotionEnabled)
            {
                if (isCollapsed)
                {
                    if (isLibrary) _isInspectorCollapsed = true;
                    else _isLibraryCollapsed = true;
                }

                SetSidePaneCollapsed(isLibrary, !isCollapsed);
                return;
            }

            var otherIsCollapsed = isLibrary ? _isInspectorCollapsed : _isLibraryCollapsed;
            if (isCollapsed && !otherIsCollapsed)
            {
                AnimateSidePane(
                    isLibrary: !isLibrary,
                    expanding: false,
                    completed: () => AnimateSidePane(isLibrary, expanding: true));
                return;
            }

            AnimateSidePane(isLibrary, expanding: isCollapsed);
            return;
        }

        if (!MotionEnabled)
        {
            SetSidePaneCollapsed(isLibrary, !isCollapsed);
            return;
        }

        AnimateSidePane(isLibrary, expanding: isCollapsed);
    }

    private void AnimateSidePane(bool isLibrary, bool expanding, Action? completed = null)
    {
        _isPaneTransitionRunning = true;
        var transitionVersion = ++_paneTransitionVersion;
        var pane = isLibrary ? (FrameworkElement)LibraryPane : InspectorPane;
        var paneHost = isLibrary ? (FrameworkElement)LibraryPaneHost : InspectorPaneHost;
        var transform = EnsureTranslateTransform(pane);
        var expandedSize = _isCompactLayout
            ? isLibrary ? GetCompactLibraryHeight() : GetCompactInspectorHeight()
            : isLibrary ? 270d : 310d;
        var currentSize = _isCompactLayout ? paneHost.ActualHeight : paneHost.ActualWidth;
        if (currentSize < 1) currentSize = expandedSize;

        if (expanding)
        {
            if (isLibrary) _isLibraryCollapsed = false;
            else _isInspectorCollapsed = false;
            paneHost.Visibility = Visibility.Visible;
        }

        UpdatePaneToggleControls();

        var duration = new Duration(TimeSpan.FromMilliseconds(235));
        var layoutEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var layoutAnimation = new DoubleAnimation(
            expanding ? 0 : currentSize,
            expanding ? expandedSize : 0,
            duration)
        {
            EasingFunction = layoutEase
        };
        layoutAnimation.Completed += (_, _) =>
        {
            if (transitionVersion != _paneTransitionVersion) return;

            paneHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            paneHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            paneHost.ClearValue(FrameworkElement.WidthProperty);
            paneHost.ClearValue(FrameworkElement.HeightProperty);
            pane.BeginAnimation(OpacityProperty, null);
            pane.Opacity = 1;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0;
            transform.Y = 0;
            _isPaneTransitionRunning = false;
            SetSidePaneCollapsed(isLibrary, !expanding);
            completed?.Invoke();
        };

        if (_isCompactLayout)
        {
            var row = isLibrary ? _compactLibraryRow : _compactInspectorRow;
            paneHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            paneHost.Height = expanding ? 0 : currentSize;
            row.Height = GridLength.Auto;
            paneHost.BeginAnimation(FrameworkElement.HeightProperty, layoutAnimation, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            var column = isLibrary ? LibraryColumn : InspectorColumn;
            paneHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            paneHost.Width = expanding ? 0 : currentSize;
            column.Width = GridLength.Auto;
            paneHost.BeginAnimation(FrameworkElement.WidthProperty, layoutAnimation, HandoffBehavior.SnapshotAndReplace);
        }

        var paneDirection = isLibrary ? -16d : 16d;
        var slideProperty = _isCompactLayout ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        pane.Opacity = expanding ? 0 : 1;
        pane.BeginAnimation(OpacityProperty, new DoubleAnimation(expanding ? 0 : 1, expanding ? 1 : 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = expanding ? EasingMode.EaseOut : EasingMode.EaseIn }
        }, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(slideProperty, new DoubleAnimation(
            expanding ? paneDirection : 0,
            expanding ? 0 : paneDirection,
            duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetSidePaneCollapsed(bool isLibrary, bool collapsed)
    {
        if (isLibrary) _isLibraryCollapsed = collapsed;
        else _isInspectorCollapsed = collapsed;
        ApplyResponsiveLayout(force: true);
    }

    private double GetCompactLibraryHeight() => Math.Clamp(ActualHeight * .15, 148d, 210d);

    private double GetCompactInspectorHeight()
    {
        const double minimumInspectorHeight = 360d;
        const double minimumMapHeight = 280d;
        var availableHeight = Math.Max(0, ActualHeight - HeaderRow.ActualHeight);
        var maximumInspectorHeight = Math.Max(minimumInspectorHeight, availableHeight - minimumMapHeight);
        var defaultHeight = Math.Clamp(ActualHeight * .60, 460d, 850d);
        return Math.Clamp(_mapInspectorPaneHeight ?? defaultHeight, minimumInspectorHeight, maximumInspectorHeight);
    }

    private void UpdateCompactPaneSizes(bool? showMapSidePanes = null)
    {
        if (!_isCompactLayout) return;
        var showPanes = showMapSidePanes ?? _activePage == WorkspacePage.Map;
        var libraryHeight = GetCompactLibraryHeight();
        var inspectorHeight = GetCompactInspectorHeight();
        _compactLibraryRow.Height = !showPanes || _isLibraryCollapsed
            ? new GridLength(0)
            : new GridLength(libraryHeight);
        _compactInspectorRow.Height = !showPanes || _isInspectorCollapsed
            ? new GridLength(0)
            : new GridLength(inspectorHeight);
        LibraryPane.Height = libraryHeight;
        InspectorPane.Height = inspectorHeight;
    }

    private double GetLandscapeInspectorWidth()
    {
        const double minimumInspectorWidth = 260d;
        const double minimumMapWidth = 420d;
        var availableWidth = ActualWidth - 72d - (_isLibraryCollapsed ? 0 : 270d);
        var maximumInspectorWidth = Math.Max(minimumInspectorWidth, availableWidth - minimumMapWidth);
        return Math.Clamp(_mapInspectorPaneWidth, minimumInspectorWidth, maximumInspectorWidth);
    }

    private void UpdateLandscapePaneSizes()
    {
        if (_isCompactLayout || _activePage != WorkspacePage.Map || _isInspectorCollapsed) return;
        var inspectorWidth = GetLandscapeInspectorWidth();
        InspectorColumn.Width = new GridLength(inspectorWidth);
        InspectorPane.Width = inspectorWidth;
    }

    private void MapTaskWidthSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isCompactLayout || _activePage != WorkspacePage.Map || _isInspectorCollapsed ||
            !double.IsFinite(e.HorizontalChange) || Math.Abs(e.HorizontalChange) < .01)
            return;

        const double minimumMapWidth = 420d;
        const double minimumInspectorWidth = 260d;
        var pairWidth = ContentColumn.ActualWidth + InspectorColumn.ActualWidth;
        var maximumInspectorWidth = pairWidth - minimumMapWidth;
        if (maximumInspectorWidth < minimumInspectorWidth) return;

        _mapInspectorPaneWidth = Math.Clamp(
            InspectorColumn.ActualWidth - e.HorizontalChange,
            minimumInspectorWidth,
            maximumInspectorWidth);
        InspectorColumn.Width = new GridLength(_mapInspectorPaneWidth);
        InspectorPane.Width = _mapInspectorPaneWidth;
        QueueMarkerReposition();
    }

    private void MapTaskHeightSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isCompactLayout || _activePage != WorkspacePage.Map || _isInspectorCollapsed ||
            !double.IsFinite(e.VerticalChange) || Math.Abs(e.VerticalChange) < .01)
            return;

        const double minimumMapHeight = 280d;
        const double minimumInspectorHeight = 360d;
        var pairHeight = _compactContentRow.ActualHeight + _compactInspectorRow.ActualHeight;
        var maximumInspectorHeight = pairHeight - minimumMapHeight;
        if (maximumInspectorHeight < minimumInspectorHeight) return;

        _mapInspectorPaneHeight = Math.Clamp(
            _compactInspectorRow.ActualHeight - e.VerticalChange,
            minimumInspectorHeight,
            maximumInspectorHeight);
        _compactInspectorRow.Height = new GridLength(_mapInspectorPaneHeight.Value);
        InspectorPane.Height = _mapInspectorPaneHeight.Value;
        QueueMarkerReposition();
    }

    private void MapTaskSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _desktopPreferences = _desktopPreferences with
        {
            MapInspectorPaneWidth = _mapInspectorPaneWidth,
            MapInspectorPaneHeight = _mapInspectorPaneHeight
        };
        DesktopPreferencesService.SaveMapWorkspaceLayout(_mapInspectorPaneWidth, _mapInspectorPaneHeight);
    }

    private void RaidStatusCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRaidStatusAnimationRunning || _isPaneTransitionRunning) return;
        SetRaidStatusCollapsed(!_isRaidStatusCollapsed, MotionEnabled);
    }

    private void SetRaidStatusCollapsed(bool collapsed, bool animate)
    {
        _isRaidStatusCollapsed = collapsed;
        RaidStatusCollapseButton.Content = collapsed ? "›" : "‹";
        RaidStatusCollapseButton.ToolTip = collapsed ? "向右展开本局状态" : "向左收起本局状态";

        RaidStatusCardHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        RaidStatusCardHost.BeginAnimation(OpacityProperty, null);
        RaidStatusCardTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);

        if (!animate)
        {
            RaidStatusCardHost.ClearValue(FrameworkElement.HeightProperty);
            RaidStatusCardHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            RaidStatusCardHost.Opacity = 1;
            RaidStatusCardTranslateTransform.X = 0;
            return;
        }

        var availableWidth = Math.Max(
            1,
            InspectorPane.ActualWidth - InspectorPane.Padding.Left - InspectorPane.Padding.Right);
        double targetHeight;
        if (collapsed)
        {
            targetHeight = RaidStatusCardHost.ActualHeight;
            if (targetHeight < 1)
            {
                SetRaidStatusCollapsed(true, animate: false);
                return;
            }
        }
        else
        {
            RaidStatusCardHost.Visibility = Visibility.Visible;
            RaidStatusCardHost.ClearValue(FrameworkElement.HeightProperty);
            RaidStatusCardHost.Measure(new Size(availableWidth, double.PositiveInfinity));
            targetHeight = Math.Max(
                1,
                RaidStatusCardHost.DesiredSize.Height -
                RaidStatusCardHost.Margin.Top -
                RaidStatusCardHost.Margin.Bottom);
            RaidStatusCardHost.Height = 0;
            RaidStatusCardHost.Opacity = 0;
            RaidStatusCardTranslateTransform.X = -36;
        }

        _isRaidStatusAnimationRunning = true;
        RaidStatusCollapseButton.IsEnabled = false;
        var duration = new Duration(TimeSpan.FromMilliseconds(235));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var heightAnimation = new DoubleAnimation(
            collapsed ? targetHeight : 0,
            collapsed ? 0 : targetHeight,
            duration)
        {
            EasingFunction = easing
        };
        heightAnimation.Completed += (_, _) =>
        {
            RaidStatusCardHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            RaidStatusCardHost.BeginAnimation(OpacityProperty, null);
            RaidStatusCardTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
            RaidStatusCardHost.ClearValue(FrameworkElement.HeightProperty);
            RaidStatusCardHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            RaidStatusCardHost.Opacity = 1;
            RaidStatusCardTranslateTransform.X = 0;
            _isRaidStatusAnimationRunning = false;
            RaidStatusCollapseButton.IsEnabled = true;
        };

        RaidStatusCardHost.BeginAnimation(
            FrameworkElement.HeightProperty,
            heightAnimation,
            HandoffBehavior.SnapshotAndReplace);
        RaidStatusCardHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(collapsed ? 1 : 0, collapsed ? 0 : 1, duration)
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = collapsed ? EasingMode.EaseIn : EasingMode.EaseOut
                }
            },
            HandoffBehavior.SnapshotAndReplace);
        RaidStatusCardTranslateTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(collapsed ? 0 : -36, collapsed ? -36 : 0, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdatePaneToggleControls()
    {
        if (LibraryPaneToggleButton is null || InspectorPaneToggleButton is null) return;
        var showMapSidePanes = _activePage == WorkspacePage.Map;
        LibraryPaneToggleButton.Visibility = showMapSidePanes ? Visibility.Visible : Visibility.Collapsed;
        InspectorPaneToggleButton.Visibility = showMapSidePanes ? Visibility.Visible : Visibility.Collapsed;
        if (!showMapSidePanes) return;
        LibraryPaneToggleButton.IsEnabled = !_isPaneTransitionRunning;
        InspectorPaneToggleButton.IsEnabled = !_isPaneTransitionRunning;

        if (_isCompactLayout)
        {
            LibraryPaneToggleButton.Width = 48;
            LibraryPaneToggleButton.Height = 26;
            LibraryPaneToggleButton.HorizontalAlignment = HorizontalAlignment.Center;
            LibraryPaneToggleButton.VerticalAlignment = VerticalAlignment.Top;
            LibraryPaneToggleButton.Margin = new Thickness(0, 8, 0, 0);
            InspectorPaneToggleButton.Width = 48;
            InspectorPaneToggleButton.Height = 26;
            InspectorPaneToggleButton.HorizontalAlignment = HorizontalAlignment.Center;
            InspectorPaneToggleButton.VerticalAlignment = VerticalAlignment.Bottom;
            InspectorPaneToggleButton.Margin = new Thickness(0, 0, 0, 8);
            LibraryPaneToggleButton.Content = _isLibraryCollapsed ? "⌄" : "⌃";
            InspectorPaneToggleButton.Content = _isInspectorCollapsed ? "⌃" : "⌄";
        }
        else
        {
            LibraryPaneToggleButton.Width = 26;
            LibraryPaneToggleButton.Height = 48;
            LibraryPaneToggleButton.HorizontalAlignment = HorizontalAlignment.Left;
            LibraryPaneToggleButton.VerticalAlignment = VerticalAlignment.Center;
            LibraryPaneToggleButton.Margin = _isLibraryCollapsed
                ? new Thickness(8, 0, 0, 0)
                : new Thickness(-13, 0, 0, 0);
            InspectorPaneToggleButton.Width = 26;
            InspectorPaneToggleButton.Height = 48;
            InspectorPaneToggleButton.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorPaneToggleButton.VerticalAlignment = VerticalAlignment.Center;
            InspectorPaneToggleButton.Margin = _isInspectorCollapsed
                ? new Thickness(0, 0, 8, 0)
                : new Thickness(0, 0, -13, 0);
            LibraryPaneToggleButton.Content = _isLibraryCollapsed ? "›" : "‹";
            InspectorPaneToggleButton.Content = _isInspectorCollapsed ? "‹" : "›";
        }

        LibraryPaneToggleButton.ToolTip = _isLibraryCollapsed ? "展开地图列表" : "收起地图列表";
        InspectorPaneToggleButton.ToolTip = _isInspectorCollapsed ? "展开本局状态" : "收起本局状态";
    }

    private void MapList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedMap = e.AddedItems.OfType<MapDefinition>().FirstOrDefault() ?? MapList.SelectedItem as MapDefinition;
        if (selectedMap is null) return;

        var previousMap = ReferenceEquals(_viewModel.SelectedMap, selectedMap)
            ? e.RemovedItems.OfType<MapDefinition>().FirstOrDefault()
            : _viewModel.SelectedMap;
        if (!ReferenceEquals(_viewModel.SelectedMap, selectedMap))
            _viewModel.SelectedMap = selectedMap;

        if (!ReferenceEquals(previousMap, selectedMap))
        {
            var reason = !IsLoaded
                ? "初始化地图"
                : _isApplyingAutomaticMapSelection ? "日志自动切换地图" : "用户切换地图";
            RuntimeLogService.Info(
                "地图",
                reason,
                $"原地图: {previousMap?.Name ?? "无"} ({previousMap?.Id ?? "none"})\n" +
                $"新地图: {selectedMap.Name} ({selectedMap.Id})\n" +
                $"地图资源: {selectedMap.ImageFilePath ?? "不可用"}\n" +
                $"点位数量: {selectedMap.Markers.Count:N0}\n" +
                $"坐标边界: {selectedMap.WorldBounds?.ToString() ?? "未配置"}");
            if (_airdropLocatorStage != AirdropLocatorStage.Inactive &&
                _airdropFirstBearing is { } firstBearing &&
                !string.Equals(firstBearing.MapId, selectedMap.Id, StringComparison.OrdinalIgnoreCase))
            {
                ResetAirdropLocalizationSamples("地图已切换，等待当前地图的新 A 点截图");
            }
        }

        ResetMapTransform();
        RefreshMapStyleControlsForSelectedMap(
            !IsLoaded ? "初始化地图" : _isApplyingAutomaticMapSelection ? "地图自动切换" : "用户切换地图");
        RefreshMapLayerControlsForSelectedMap(
            !IsLoaded ? "初始化地图" : _isApplyingAutomaticMapSelection ? "地图自动切换" : "用户切换地图");
        UpdateTaskConfiguration();
        UpdateMarkers();
        BeginMapTransition(previousMap);
        _ = _teamSyncFeature?.PublishCurrentLocationAsync();
    }

    private void MapTransformHost_SizeChanged(object sender, SizeChangedEventArgs e) => QueueMarkerReposition();

    private void MapStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMapStyleSelection ||
            _viewModel.SelectedMap is not { } map ||
            MapStyleComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string style)
            return;

        _selectedMapStyles[map.Id] = style;
        _desktopPreferences = _desktopPreferences with
        {
            SelectedMapStyles = new Dictionary<string, string>(_selectedMapStyles, StringComparer.OrdinalIgnoreCase)
        };
        _ = DesktopPreferencesService.SaveMapStyleState(_selectedMapStyles);
        _ = ApplyMapBaseStyleAsync(style, "用户切换底图", showToast: true);
    }

    private void RefreshMapStyleControlsForSelectedMap(string reason)
    {
        if (MapStyleControls is null || MapStyleComboBox is null) return;
        var map = _viewModel.SelectedMap;
        var satelliteMapAvailable = map is not null &&
                                    MapBaseProjectionService.TryGetWebMap(map.Id, out var satelliteMap) &&
                                    File.Exists(GetWebMapImagePath(satelliteMap));
        MapStyleControls.Visibility = satelliteMapAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        var style = "2d";
        if (map is not null && _selectedMapStyles.TryGetValue(map.Id, out var savedStyle))
        {
            if (MapBaseProjectionService.IsSatelliteMapStyle(savedStyle) && satelliteMapAvailable)
                style = "satellite-map";
        }

        _suppressMapStyleSelection = true;
        SelectMapStyleItem(style);
        _suppressMapStyleSelection = false;
        _ = ApplyMapBaseStyleAsync(style, reason, showToast: false);
    }

    private async Task ApplyMapBaseStyleAsync(string style, string reason, bool showToast)
    {
        var version = ++_mapBaseStyleLoadVersion;
        if (_viewModel.SelectedMap is not { } map) return;
        if (MapBaseProjectionService.IsSatelliteMapStyle(style)) style = "satellite-map";
        string? path = null;
        if (string.Equals(style, "satellite-map", StringComparison.OrdinalIgnoreCase) &&
            MapBaseProjectionService.TryGetWebMap(map.Id, out var foundWebMap))
        {
            path = GetWebMapImagePath(foundWebMap);
        }
        if (path is null)
        {
            MapStyleComboBox.IsEnabled = true;
            MapBaseVariantImage.Source = null;
            MapBaseVariantImage.Visibility = Visibility.Collapsed;
            MapImage.Visibility = Visibility.Visible;
            _activeMapStyle = "2d";
            _activeMapStyleMapId = map.Id;
            RefreshMapLayerControlsForSelectedMap(reason);
            QueueMarkerReposition();
            UpdateMiniMap(GetVisibleMarkers());
            if (showToast) ShowToast("已切换至 2D 地图");
            return;
        }

        if (string.Equals(_activeMapStyle, style, StringComparison.Ordinal) &&
            string.Equals(_activeMapStyleMapId, map!.Id, StringComparison.OrdinalIgnoreCase) &&
            MapBaseVariantImage.Source is not null)
        {
            if (showToast) ShowToast("当前已是卫星地图");
            return;
        }

        MapStyleComboBox.IsEnabled = false;
        var image = await GetOrLoadMapBaseStyleImageAsync(path);
        if (version != _mapBaseStyleLoadVersion || _isWindowClosing ||
            !string.Equals(_viewModel.SelectedMap?.Id, map!.Id, StringComparison.OrdinalIgnoreCase))
            return;
        MapStyleComboBox.IsEnabled = true;

        if (image is null)
        {
            _selectedMapStyles[map.Id] = "2d";
            _desktopPreferences = _desktopPreferences with
            {
                SelectedMapStyles = new Dictionary<string, string>(_selectedMapStyles, StringComparer.OrdinalIgnoreCase)
            };
            _ = DesktopPreferencesService.SaveMapStyleState(_selectedMapStyles);
            _suppressMapStyleSelection = true;
            SelectMapStyleItem("2d");
            _suppressMapStyleSelection = false;
            MapBaseVariantImage.Source = null;
            MapBaseVariantImage.Visibility = Visibility.Collapsed;
            MapImage.Visibility = Visibility.Visible;
            _activeMapStyle = "2d";
            _activeMapStyleMapId = map.Id;
            RefreshMapLayerControlsForSelectedMap("底图加载失败");
            QueueMarkerReposition();
            UpdateMiniMap(GetVisibleMarkers());
            ShowToast("卫星地图加载失败，已回到 2D 地图");
            return;
        }

        MapBaseVariantImage.Source = image;
        MapBaseVariantImage.Visibility = Visibility.Visible;
        MapImage.Visibility = Visibility.Collapsed;
        _activeMapStyle = style;
        _activeMapStyleMapId = map.Id;
        RefreshMapLayerControlsForSelectedMap(reason);
        QueueMarkerReposition();
        UpdateMiniMap(GetVisibleMarkers());
        RuntimeLogService.Info(
            "地图底图",
            $"{map.Name}卫星地图加载完成",
            $"原因: {reason}\n解码尺寸: {(image as BitmapSource)?.PixelWidth:N0} × {(image as BitmapSource)?.PixelHeight:N0}\n" +
            $"文件: {path}\n来源: https://www.tarkov-helper.cn/maps");
        if (showToast) ShowToast($"已切换至{map.Name}卫星地图");
    }

    private void SelectMapStyleItem(string style)
    {
        MapStyleComboBox.SelectedItem = MapStyleComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, style, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetWebMapImagePath(WebMapDefinition definition) => System.IO.Path.Combine(
        AppContext.BaseDirectory,
        definition.Image.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private void MapLayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMapLayerSelection || MapLayerComboBox.SelectedItem is not MapLayerChoice choice) return;

        _autoMapLayerEnabled = false;
        UpdateMapLayerAutoButton();
        SelectMapLayer(choice.Layer, "用户手动选择", persist: true, animate: true);
        ShowToast(choice.Layer is null ? "已切换至地面图" : $"已切换至{choice.Name}");
    }

    private void MapLayerAutoButton_Click(object sender, RoutedEventArgs e)
    {
        _autoMapLayerEnabled = !_autoMapLayerEnabled;
        UpdateMapLayerAutoButton();
        PersistMapLayerState();
        if (_autoMapLayerEnabled &&
            TryGetCurrentLayerCoordinate(out var mapId, out var coordinate))
        {
            ApplyAutomaticMapLayer(mapId, coordinate, "用户开启自动切层");
            ShowToast(_activeMapLayer is null ? "自动切层已开启 · 当前为地面图" : $"自动切层已开启 · {_activeMapLayer.Name}");
        }
        else
        {
            ShowToast(_autoMapLayerEnabled ? "自动切层已开启，等待新坐标" : "自动切层已关闭");
        }
    }

    private void RefreshMapLayerControlsForSelectedMap(string reason)
    {
        if (MapLayerControls is null || MapLayerComboBox is null) return;

        var map = _viewModel.SelectedMap;
        var nextMapId = map?.Id;
        var mapChanged = !string.Equals(_mapLayerMapId, nextMapId, StringComparison.OrdinalIgnoreCase);
        var nextLayerStyle = map is not null &&
                             string.Equals(_activeMapStyleMapId, map.Id, StringComparison.OrdinalIgnoreCase) &&
                             MapBaseProjectionService.IsSatelliteMapStyle(_activeMapStyle)
            ? "satellite-map"
            : "2d";
        var styleChanged = !string.Equals(_mapLayerStyle, nextLayerStyle, StringComparison.OrdinalIgnoreCase);
        var contextChanged = mapChanged || styleChanged;
        _mapLayerMapId = nextMapId;
        _mapLayerStyle = nextLayerStyle;

        var layers = map is null ? [] : GetAvailableMapLayers(map.Id);
        if (map is null || layers.Count == 0)
        {
            _activeMapLayer = null;
            MapLayerControls.Visibility = Visibility.Collapsed;
            _suppressMapLayerSelection = true;
            MapLayerComboBox.ItemsSource = null;
            _suppressMapLayerSelection = false;
            ClearMapLayerVisual(animate: !contextChanged);
            UpdateMapLayerAutoButton();
            return;
        }

        var surfaceName = nextLayerStyle == "satellite-map" &&
                          MapBaseProjectionService.TryGetWebMap(map.Id, out var satelliteMap)
            ? satelliteMap.SurfaceName ?? "地面"
            : _mapLayerCatalog.SurfaceNamesByMap.TryGetValue(map.Id, out var configuredSurfaceName)
                ? configuredSurfaceName
                : "地面";
        var choices = new List<MapLayerChoice> { new("", surfaceName, null) };
        choices.AddRange(layers.Select(layer => new MapLayerChoice(layer.Id, layer.Name, layer)));
        MapLayerControls.Visibility = Visibility.Visible;
        _suppressMapLayerSelection = true;
        MapLayerComboBox.ItemsSource = choices;
        _suppressMapLayerSelection = false;

        MapLayerDefinition? target = null;
        if (_autoMapLayerEnabled &&
            TryGetCurrentLayerCoordinate(out var coordinateMapId, out var coordinate) &&
            string.Equals(coordinateMapId, map.Id, StringComparison.OrdinalIgnoreCase))
        {
            target = FindAutomaticMapLayer(layers, coordinate);
        }
        else if (_selectedMapLayerIds.TryGetValue(map.Id, out var selectedId))
        {
            target = layers.FirstOrDefault(layer => string.Equals(layer.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }

        if (contextChanged) ClearMapLayerVisual(animate: false);
        SelectMapLayer(target, reason, persist: false, animate: !contextChanged, forceVisualRefresh: contextChanged);
        UpdateMapLayerAutoButton();
    }

    private IReadOnlyList<MapLayerDefinition> GetAvailableMapLayers(string mapId)
    {
        var satelliteActive = string.Equals(_activeMapStyleMapId, mapId, StringComparison.OrdinalIgnoreCase) &&
                              MapBaseProjectionService.IsSatelliteMapStyle(_activeMapStyle);
        if (!satelliteActive)
            return _mapLayerCatalog.LayersByMap.TryGetValue(mapId, out var regularLayers) ? regularLayers : [];

        if (_satelliteMapLayers.TryGetValue(mapId, out var cached)) return cached;
        if (!MapBaseProjectionService.TryGetWebMap(mapId, out var map) || map.Layers is not { Length: > 0 })
            return _satelliteMapLayers[mapId] = [];

        var layers = map.Layers
            .Where(layer => !string.IsNullOrWhiteSpace(layer.Id) && !string.IsNullOrWhiteSpace(layer.Image))
            .Select(layer => new MapLayerDefinition
            {
                MapId = mapId,
                Id = layer.Id,
                Name = string.IsNullOrWhiteSpace(layer.Name) ? layer.Id : layer.Name,
                ImageFileName = System.IO.Path.GetFileName(layer.Image),
                ImageFilePath = System.IO.Path.Combine(AppContext.BaseDirectory, layer.Image.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                Extents = (layer.Extents ?? [])
                    .Where(extent => extent.Height is { Length: >= 2 })
                    .Select(extent => new MapLayerExtent
                    {
                        MinimumHeight = extent.Height[0],
                        MaximumHeight = extent.Height[1],
                        Bounds = (extent.Bounds ?? [])
                            .Where(bounds => bounds is { Length: >= 2 } && bounds[0] is { Length: >= 2 } && bounds[1] is { Length: >= 2 })
                            .Select(bounds => new MapLayerWorldBounds(bounds[0][0], bounds[0][1], bounds[1][0], bounds[1][1]))
                            .ToArray()
                    })
                    .ToArray()
            })
            .Where(layer => File.Exists(layer.ImageFilePath))
            .ToArray();
        return _satelliteMapLayers[mapId] = layers;
    }

    private void SelectMapLayer(
        MapLayerDefinition? layer,
        string reason,
        bool persist,
        bool animate,
        bool forceVisualRefresh = false)
    {
        var map = _viewModel.SelectedMap;
        if (map is null) return;
        if (layer is not null && !string.Equals(layer.MapId, map.Id, StringComparison.OrdinalIgnoreCase)) return;

        var unchanged = string.Equals(_activeMapLayer?.MapId, layer?.MapId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(_activeMapLayer?.Id, layer?.Id, StringComparison.OrdinalIgnoreCase);
        _activeMapLayer = layer;
        if (layer is null)
            _selectedMapLayerIds.Remove(map.Id);
        else
            _selectedMapLayerIds[map.Id] = layer.Id;

        SelectMapLayerChoice(layer);
        if (persist) PersistMapLayerState();
        if (!unchanged || forceVisualRefresh || (layer is not null && MapLayerImage.Source is null))
            _ = ApplyMapLayerVisualAsync(layer, reason, animate);

        UpdateMarkers();
    }

    private void SelectMapLayerChoice(MapLayerDefinition? layer)
    {
        if (MapLayerComboBox.ItemsSource is not IEnumerable<MapLayerChoice> choices) return;
        var choice = choices.FirstOrDefault(item => string.Equals(item.Id, layer?.Id ?? "", StringComparison.OrdinalIgnoreCase));
        if (choice is null) return;

        _suppressMapLayerSelection = true;
        MapLayerComboBox.SelectedItem = choice;
        _suppressMapLayerSelection = false;
    }

    private void UpdateMapLayerAutoButton()
    {
        if (MapLayerAutoButton is null || MapLayerAutoDot is null) return;
        MapLayerAutoButton.Background = (Brush)FindResource("RaisedBrush");
        MapLayerAutoButton.BorderBrush = (Brush)FindResource(_autoMapLayerEnabled ? "AmberBrush" : "LineBrightBrush");
        MapLayerAutoButton.Foreground = (Brush)FindResource(_autoMapLayerEnabled ? "AmberBrush" : "TextDimBrush");
        MapLayerAutoDot.Fill = (Brush)FindResource(_autoMapLayerEnabled ? "GreenBrush" : "TextFaintBrush");
    }

    private void PersistMapLayerState()
    {
        var savedLayers = new Dictionary<string, string>(_selectedMapLayerIds, StringComparer.OrdinalIgnoreCase);
        _desktopPreferences = _desktopPreferences with
        {
            AutoSwitchMapLayer = _autoMapLayerEnabled,
            SelectedMapLayers = savedLayers
        };
        DesktopPreferencesService.SaveMapLayerState(_autoMapLayerEnabled, savedLayers);
    }

    private bool TryGetCurrentLayerCoordinate(out string mapId, out LiveCoordinate coordinate)
    {
        mapId = "";
        coordinate = null!;
        if (_lastRaidSnapshot is not { } snapshot ||
            !IsMapEvidenceAuthoritative(snapshot) ||
            !IsCoordinateFromCurrentRaid(snapshot) ||
            snapshot.Coordinate is not { } current ||
            string.IsNullOrWhiteSpace(snapshot.MapKey))
            return false;

        mapId = snapshot.MapKey;
        coordinate = current;
        return true;
    }

    private MapLayerDefinition? FindAutomaticMapLayer(
        IReadOnlyList<MapLayerDefinition> layers,
        LiveCoordinate coordinate)
    {
        if (_activeMapLayer is { } active &&
            layers.Contains(active) &&
            active.Extents.Any(extent =>
                coordinate.Y >= Math.Min(extent.MinimumHeight, extent.MaximumHeight) - .35d &&
                coordinate.Y <= Math.Max(extent.MinimumHeight, extent.MaximumHeight) + .35d &&
                (extent.Bounds.Count == 0 || extent.Bounds.Any(bounds => bounds.Contains(coordinate.X, coordinate.Z)))))
            return active;

        return layers
            .Where(layer => layer.MatchesPosition(coordinate.X, coordinate.Y, coordinate.Z))
            .OrderByDescending(layer => layer.Extents
                .Where(extent => extent.Matches(coordinate.X, coordinate.Y, coordinate.Z))
                .Select(extent => Math.Min(extent.MinimumHeight, extent.MaximumHeight))
                .DefaultIfEmpty(double.MinValue)
                .Max())
            .FirstOrDefault();
    }

    private void ApplyAutomaticMapLayer(string mapId, LiveCoordinate coordinate, string reason)
    {
        var map = _viewModel.SelectedMap;
        if (!_autoMapLayerEnabled ||
            map is null ||
            !string.Equals(map.Id, mapId, StringComparison.OrdinalIgnoreCase))
            return;
        var layers = GetAvailableMapLayers(map.Id);
        if (layers.Count == 0) return;

        var target = FindAutomaticMapLayer(layers, coordinate);
        var unchanged = string.Equals(_activeMapLayer?.Id, target?.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(_activeMapLayer?.MapId, target?.MapId, StringComparison.OrdinalIgnoreCase);
        if (unchanged) return;

        SelectMapLayer(target, reason, persist: true, animate: true);
        RuntimeLogService.Info(
            "地图楼层",
            target is null ? "自动切换至地面图" : $"自动切换至{target.Name}",
            $"地图: {map.Name} ({map.Id})\n" +
            $"坐标: X {coordinate.X:0.###} · Y {coordinate.Y:0.###} · Z {coordinate.Z:0.###}\n" +
            $"匹配楼层: {target?.Id ?? "surface"}");
    }

    private async Task ApplyMapLayerVisualAsync(MapLayerDefinition? layer, string reason, bool animate)
    {
        var version = ++_mapLayerLoadVersion;
        if (layer is null)
        {
            ClearMapLayerVisual(animate);
            UpdateMiniMap(GetVisibleMarkers());
            return;
        }

        MapLayerComboBox.IsEnabled = false;
        var previousImage = MapLayerImage.Source;
        var image = await GetOrLoadMapLayerImageAsync(layer);
        if (version != _mapLayerLoadVersion || _isWindowClosing ||
            !string.Equals(_activeMapLayer?.MapId, layer.MapId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_activeMapLayer?.Id, layer.Id, StringComparison.OrdinalIgnoreCase))
            return;

        MapLayerComboBox.IsEnabled = true;
        if (image is null)
        {
            RuntimeLogService.Warning(
                "地图楼层",
                "楼层图加载失败，已回退至地面图",
                $"地图: {layer.MapId}\n楼层: {layer.Name} ({layer.Id})\n文件: {layer.ImageFilePath}");
            _activeMapLayer = null;
            _selectedMapLayerIds.Remove(layer.MapId);
            SelectMapLayerChoice(null);
            PersistMapLayerState();
            ClearMapLayerVisual(animate: false);
            UpdateMarkers();
            ShowToast($"{layer.Name}加载失败，已回退至地面图");
            return;
        }

        MapLayerImage.BeginAnimation(OpacityProperty, null);
        MapLayerTransitionImage.BeginAnimation(OpacityProperty, null);
        if (animate && MotionEnabled && previousImage is not null && !ReferenceEquals(previousImage, image))
        {
            MapLayerTransitionImage.Source = previousImage;
            MapLayerTransitionImage.Opacity = .98;
            MapLayerTransitionImage.Visibility = Visibility.Visible;
        }
        else
        {
            MapLayerTransitionImage.Source = null;
            MapLayerTransitionImage.Opacity = 0;
            MapLayerTransitionImage.Visibility = Visibility.Collapsed;
        }

        MapLayerImage.Source = image;
        MapLayerImage.Visibility = Visibility.Visible;
        if (animate && MotionEnabled && !ReferenceEquals(previousImage, image))
        {
            MapLayerImage.Opacity = 0;
            AnimateOpacity(MapLayerImage, .98, 210, version);
            if (MapLayerTransitionImage.Visibility == Visibility.Visible)
                AnimateOpacity(MapLayerTransitionImage, 0, 210, version, clearLayerTransition: true);
            AnimateOpacity(MapImage, .14, 210, version);
        }
        else
        {
            MapLayerImage.Opacity = .98;
            MapImage.BeginAnimation(OpacityProperty, null);
            MapImage.Opacity = .14;
        }

        RuntimeLogService.Info(
            "地图楼层",
            "楼层图加载完成",
            $"地图: {layer.MapId}\n楼层: {layer.Name} ({layer.Id})\n原因: {reason}\n文件: {layer.ImageFilePath}");
        UpdateMiniMap(GetVisibleMarkers());
    }

    private void ClearMapLayerVisual(bool animate)
    {
        var version = ++_mapLayerLoadVersion;
        MapLayerComboBox.IsEnabled = true;
        var previousImage = MapLayerImage.Source;
        MapLayerImage.BeginAnimation(OpacityProperty, null);
        MapLayerTransitionImage.BeginAnimation(OpacityProperty, null);
        if (animate && MotionEnabled && previousImage is not null)
        {
            MapLayerTransitionImage.Source = previousImage;
            MapLayerTransitionImage.Opacity = .98;
            MapLayerTransitionImage.Visibility = Visibility.Visible;
            MapLayerImage.Source = null;
            MapLayerImage.Opacity = 0;
            MapLayerImage.Visibility = Visibility.Collapsed;
            AnimateOpacity(MapLayerTransitionImage, 0, 190, version, clearLayerTransition: true);
            AnimateOpacity(MapImage, .9, 190, version);
        }
        else
        {
            MapLayerImage.Source = null;
            MapLayerImage.Opacity = 0;
            MapLayerImage.Visibility = Visibility.Collapsed;
            MapLayerTransitionImage.Source = null;
            MapLayerTransitionImage.Opacity = 0;
            MapLayerTransitionImage.Visibility = Visibility.Collapsed;
            MapImage.BeginAnimation(OpacityProperty, null);
            MapImage.Opacity = .9;
        }
    }

    private void AnimateOpacity(
        UIElement element,
        double target,
        int durationMilliseconds,
        int version,
        bool clearLayerTransition = false)
    {
        var animation = new DoubleAnimation(element.Opacity, target, new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (version != _mapLayerLoadVersion) return;
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = target;
            if (clearLayerTransition && ReferenceEquals(element, MapLayerTransitionImage))
            {
                MapLayerTransitionImage.Source = null;
                MapLayerTransitionImage.Visibility = Visibility.Collapsed;
            }
        };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private async Task<ImageSource?> GetOrLoadMapLayerImageAsync(MapLayerDefinition layer)
    {
        if (_mapLayerImageCache.TryGetValue(layer.ImageFilePath, out var cached))
        {
            TouchMapLayerImage(layer.ImageFilePath);
            return cached;
        }

        if (!_mapLayerImageLoads.TryGetValue(layer.ImageFilePath, out var loadTask))
        {
            loadTask = Task.Run(() => LoadMapLayerImage(layer.ImageFilePath));
            _mapLayerImageLoads[layer.ImageFilePath] = loadTask;
        }

        ImageSource? image;
        try
        {
            image = await loadTask;
        }
        finally
        {
            if (_mapLayerImageLoads.TryGetValue(layer.ImageFilePath, out var current) &&
                ReferenceEquals(current, loadTask))
                _mapLayerImageLoads.Remove(layer.ImageFilePath);
        }

        if (image is null) return null;
        _mapLayerImageCache[layer.ImageFilePath] = image;
        TouchMapLayerImage(layer.ImageFilePath);
        TrimMapLayerImageCache();
        return image;
    }

    private void TouchMapLayerImage(string path)
    {
        var node = _mapLayerImageLru.Find(path);
        if (node is not null) _mapLayerImageLru.Remove(node);
        _mapLayerImageLru.AddFirst(path);
    }

    private void TrimMapLayerImageCache()
    {
        while (_mapLayerImageLru.Count > MapLayerImageCacheCapacity)
        {
            var node = _mapLayerImageLru.Last;
            if (node is null) return;
            _mapLayerImageLru.RemoveLast();
            if (string.Equals(node.Value, _activeMapLayer?.ImageFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _mapLayerImageLru.AddFirst(node.Value);
                continue;
            }
            _mapLayerImageCache.Remove(node.Value);
        }
    }

    private static ImageSource? LoadMapLayerImage(string filePath)
        => LoadMapVisualImage(filePath, "地图楼层", "楼层图片解码失败");

    private async Task<ImageSource?> GetOrLoadMapBaseStyleImageAsync(string path)
    {
        if (_mapBaseStyleImageCache.TryGetValue(path, out var cached))
        {
            TouchMapBaseStyleImage(path);
            return cached;
        }

        if (!_mapBaseStyleImageLoads.TryGetValue(path, out var loadTask))
        {
            loadTask = Task.Run(() => LoadMapBaseStyleImage(path));
            _mapBaseStyleImageLoads[path] = loadTask;
        }

        ImageSource? image;
        try
        {
            image = await loadTask;
        }
        finally
        {
            if (_mapBaseStyleImageLoads.TryGetValue(path, out var current) && ReferenceEquals(current, loadTask))
                _mapBaseStyleImageLoads.Remove(path);
        }

        if (image is null) return null;
        _mapBaseStyleImageCache[path] = image;
        TouchMapBaseStyleImage(path);
        while (_mapBaseStyleImageLru.Count > MapBaseStyleImageCacheCapacity)
        {
            var last = _mapBaseStyleImageLru.Last;
            if (last is null) break;
            _mapBaseStyleImageLru.RemoveLast();
            _mapBaseStyleImageCache.Remove(last.Value);
        }
        return image;
    }

    private void TouchMapBaseStyleImage(string path)
    {
        var node = _mapBaseStyleImageLru.Find(path);
        if (node is not null) _mapBaseStyleImageLru.Remove(node);
        _mapBaseStyleImageLru.AddFirst(path);
    }

    private static ImageSource? LoadMapBaseStyleImage(string filePath)
        => LoadMapVisualImage(filePath, "地图底图", "卫星图解码失败");

    private static ImageSource? LoadMapVisualImage(string filePath, string category, string failureMessage)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var pixelWidth = GetMapLayerPixelWidth(filePath);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (pixelWidth > MapLayerDecodePixelWidthLimit)
                image.DecodePixelWidth = MapLayerDecodePixelWidthLimit;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error(category, failureMessage, exception, $"文件: {filePath}");
            return null;
        }
    }

    private static int GetMapLayerPixelWidth(string filePath)
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

    private void PointDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        _isPointDisplayPanelExpanded = !_isPointDisplayPanelExpanded;
        AnimatePointDisplayPanel(_isPointDisplayPanelExpanded);
    }

    private void AnimatePointDisplayPanel(bool expanding)
    {
        var transitionVersion = ++_pointDisplayTransitionVersion;
        var transform = EnsureTranslateTransform(PointDisplayPanel);
        var currentHeight = PointDisplayPanelHost.Visibility == Visibility.Visible
            ? Math.Max(0, PointDisplayPanelHost.ActualHeight)
            : 0;
        var currentOpacity = PointDisplayPanelHost.Visibility == Visibility.Visible
            ? PointDisplayPanel.Opacity
            : 0;
        var currentOffset = PointDisplayPanelHost.Visibility == Visibility.Visible
            ? transform.Y
            : -5;

        PointDisplayPanelHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        PointDisplayPanel.BeginAnimation(OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);

        if (!MotionEnabled)
        {
            SetPointDisplayPanelState(expanding);
            return;
        }

        if (expanding)
        {
            PointDisplayPanelHost.Visibility = Visibility.Visible;
            PointDisplayPanelHost.Height = double.NaN;
            var parentWidth = (PointDisplayPanelHost.Parent as FrameworkElement)?.ActualWidth ?? ActualWidth;
            PointDisplayPanelHost.Measure(new Size(Math.Max(1, parentWidth), double.PositiveInfinity));
            var targetHeight = Math.Max(1, PointDisplayPanelHost.DesiredSize.Height);

            PointDisplayPanelHost.Height = currentHeight;
            PointDisplayPanel.Opacity = currentOpacity;
            PointDisplayPanel.IsHitTestVisible = true;
            transform.Y = currentOffset;

            var duration = new Duration(TimeSpan.FromMilliseconds(215));
            var heightAnimation = new DoubleAnimation(currentHeight, targetHeight, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            heightAnimation.Completed += (_, _) =>
                CompletePointDisplayPanelAnimation(transitionVersion, expanded: true);
            PointDisplayPanelHost.BeginAnimation(
                FrameworkElement.HeightProperty,
                heightAnimation,
                HandoffBehavior.SnapshotAndReplace);
            PointDisplayPanel.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(currentOpacity, 1, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(currentOffset, 0, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                HandoffBehavior.SnapshotAndReplace);
            return;
        }

        if (currentHeight <= 0)
        {
            SetPointDisplayPanelState(expanded: false);
            return;
        }

        PointDisplayPanelHost.Visibility = Visibility.Visible;
        PointDisplayPanelHost.Height = currentHeight;
        PointDisplayPanel.Opacity = currentOpacity;
        PointDisplayPanel.IsHitTestVisible = false;
        transform.Y = currentOffset;

        var collapseDuration = new Duration(TimeSpan.FromMilliseconds(170));
        var collapseAnimation = new DoubleAnimation(currentHeight, 0, collapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        collapseAnimation.Completed += (_, _) =>
            CompletePointDisplayPanelAnimation(transitionVersion, expanded: false);
        PointDisplayPanelHost.BeginAnimation(
            FrameworkElement.HeightProperty,
            collapseAnimation,
            HandoffBehavior.SnapshotAndReplace);
        PointDisplayPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentOpacity, 0, collapseDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            },
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(currentOffset, -5, collapseDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void CompletePointDisplayPanelAnimation(int transitionVersion, bool expanded)
    {
        if (transitionVersion != _pointDisplayTransitionVersion ||
            _isPointDisplayPanelExpanded != expanded)
            return;

        PointDisplayPanelHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        PointDisplayPanel.BeginAnimation(OpacityProperty, null);
        if (PointDisplayPanel.RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.YProperty, null);
        SetPointDisplayPanelState(expanded);
    }

    private void SetPointDisplayPanelState(bool expanded)
    {
        var transform = EnsureTranslateTransform(PointDisplayPanel);
        PointDisplayPanelHost.Height = expanded ? double.NaN : 0;
        PointDisplayPanelHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PointDisplayPanel.Opacity = expanded ? 1 : 0;
        PointDisplayPanel.IsHitTestVisible = expanded;
        transform.Y = expanded ? 0 : -5;
    }

    private void PointDisplayCheckChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        PointDisplayCountText.Text = PointDisplayChecks().Count(check => check.IsChecked == true).ToString();
        UpdateMarkers();
    }

    private void ResetPointDisplay_Click(object sender, RoutedEventArgs e)
    {
        ExtractPointDisplayCheck.IsChecked = true;
        TaskPointDisplayCheck.IsChecked = true;
        KeyRoomPointDisplayCheck.IsChecked = false;
        SwitchPointDisplayCheck.IsChecked = false;
        SeasonDocumentPointDisplayCheck.IsChecked = false;
        BtrPointDisplayCheck.IsChecked = true;
        PointDisplayCountText.Text = PointDisplayChecks().Count(check => check.IsChecked == true).ToString(CultureInfo.InvariantCulture);
        UpdateMarkers();
    }

    private IEnumerable<CheckBox> PointDisplayChecks() => [ExtractPointDisplayCheck, TaskPointDisplayCheck, KeyRoomPointDisplayCheck, SwitchPointDisplayCheck, SeasonDocumentPointDisplayCheck, BtrPointDisplayCheck];

    private async void AirdropLocatorButton_Click(object sender, RoutedEventArgs e) => await ToggleAirdropLocalizationAsync();

    private void AirdropResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_airdropLocatorStage == AirdropLocatorStage.Inactive) return;
        ResetAirdropLocalizationSamples("已重置，等待新的 A 点截图");
        ShowToast("空投定位已重置");
    }

    private void AirdropExitButton_Click(object sender, RoutedEventArgs e) => StopAirdropLocalization(showToast: true);

    private async Task ToggleAirdropLocalizationAsync()
    {
        if (_airdropLocatorStage == AirdropLocatorStage.Inactive)
            await StartAirdropLocalizationAsync();
        else
            StopAirdropLocalization(showToast: true);
    }

    private async Task StartAirdropLocalizationAsync()
    {
        if (_requiresInitialPathSetup)
        {
            ShowWorkspacePage(WorkspacePage.Settings);
            ShowToast("请先选择截图目录和游戏日志目录");
            return;
        }

        _airdropLocatorStage = AirdropLocatorStage.WaitingForFirst;
        _airdropFirstBearing = null;
        _airdropSecondBearing = null;
        _airdropEstimate = null;
        _airdropIssue = null;
        _airdropActivatedAt = DateTimeOffset.Now;
        _airdropLastProcessedFileName = (_lastRaidSnapshot?.Coordinate ?? _viewModel.LiveCoordinate)?.FileName;
        ShowWorkspacePage(WorkspacePage.Map);
        UpdateAirdropLocatorUi();
        UpdateMarkers();

        RuntimeLogService.Info(
            "空投定位",
            "开启空投定位模式",
            $"快捷键来源: {(_ctrlTapListenerActive ? "全局 Ctrl 单击" : "界面按钮")}\n" +
            $"启动时最近截图: {_airdropLastProcessedFileName ?? "无"}\n" +
            "下一张新坐标截图将记录为 A 点");
        await EnsureAirdropMonitoringAsync();
        ShowToast("空投定位：瞄准空投后按 · 截取 A 点");
    }

    private async Task EnsureAirdropMonitoringAsync()
    {
        var changed = false;
        if (!_isListening)
        {
            _isListening = true;
            _lastAppliedRaidMapSignature = null;
            changed = true;
        }
        if (!_isAutoRefreshEnabled)
        {
            _isAutoRefreshEnabled = true;
            changed = true;
        }

        if (changed) _raidMonitor.RequestScreenshotRescan(restartWatcher: true);
        UpdateListeningControls();
        await RefreshLocalRaidDataAsync();
        if (_isListening && _isAutoRefreshEnabled) _autoRefreshTimer.Start();
        if (changed)
        {
            RuntimeLogService.Info(
                "空投定位",
                "已自动开启监听与自动刷新",
                $"刷新间隔: {_autoRefreshTimer.Interval.TotalSeconds:0.##} 秒");
        }
    }

    private void StopAirdropLocalization(bool showToast)
    {
        if (_airdropLocatorStage == AirdropLocatorStage.Inactive) return;
        _airdropLocatorStage = AirdropLocatorStage.Inactive;
        _airdropFirstBearing = null;
        _airdropSecondBearing = null;
        _airdropEstimate = null;
        _airdropIssue = null;
        _airdropLastProcessedFileName = null;
        UpdateAirdropLocatorUi();
        UpdateMarkers();
        RuntimeLogService.Info("空投定位", "退出空投定位模式", "已清除 A/B 射线和估算点");
        if (showToast) ShowToast("已退出空投定位模式");
    }

    private void ResetAirdropLocalizationSamples(string? issue = null)
    {
        _airdropLocatorStage = AirdropLocatorStage.WaitingForFirst;
        _airdropFirstBearing = null;
        _airdropSecondBearing = null;
        _airdropEstimate = null;
        _airdropIssue = issue;
        _airdropActivatedAt = DateTimeOffset.Now;
        _airdropLastProcessedFileName = (_lastRaidSnapshot?.Coordinate ?? _viewModel.LiveCoordinate)?.FileName;
        UpdateAirdropLocatorUi();
        UpdateMarkers();
    }

    private void ProcessAirdropCoordinate(string mapId, LiveCoordinate coordinate)
    {
        if (_airdropLocatorStage is AirdropLocatorStage.Inactive or AirdropLocatorStage.Solved) return;
        if (string.Equals(coordinate.FileName, _airdropLastProcessedFileName, StringComparison.OrdinalIgnoreCase)) return;

        _airdropLastProcessedFileName = coordinate.FileName;
        if (coordinate.CapturedAt < _airdropActivatedAt - TimeSpan.FromSeconds(2)) return;

        var sample = AirdropBearingSample.FromCoordinate(mapId, coordinate);
        if (_airdropLocatorStage == AirdropLocatorStage.WaitingForFirst ||
            _airdropFirstBearing is null ||
            !string.Equals(_airdropFirstBearing.MapId, mapId, StringComparison.OrdinalIgnoreCase))
        {
            _airdropFirstBearing = sample;
            _airdropSecondBearing = null;
            _airdropEstimate = null;
            _airdropIssue = null;
            _airdropLocatorStage = AirdropLocatorStage.WaitingForSecond;
            RuntimeLogService.Info(
                "空投定位",
                "已记录 A 点射线",
                $"地图: {mapId}\n文件: {coordinate.FileName}\n坐标: X {coordinate.X:0.##} · Z {coordinate.Z:0.##}\n朝向: {coordinate.YawDegrees:0.##}°");
            UpdateAirdropLocatorUi();
            UpdateMarkers();
            ShowToast("A 点已记录，请移动后瞄准空投再按 ·");
            return;
        }

        _airdropSecondBearing = sample;
        _airdropEstimate = null;
        if (!AirdropTriangulationService.TryEstimate(_airdropFirstBearing, sample, out var estimate, out var failureReason))
        {
            _airdropIssue = failureReason;
            RuntimeLogService.Warning(
                "空投定位",
                "B 点无法形成有效交会",
                $"文件: {coordinate.FileName}\n原因: {failureReason}");
            UpdateAirdropLocatorUi();
            UpdateMarkers();
            ShowToast(failureReason);
            return;
        }

        var map = _viewModel.Maps.FirstOrDefault(item => string.Equals(item.Id, mapId, StringComparison.OrdinalIgnoreCase));
        if (map?.WorldBounds is not { } bounds || estimate is null || !bounds.TryProject(estimate.X, estimate.Z, out _, out _))
        {
            _airdropIssue = "两条射线的交点位于当前地图范围之外，请重新截取 B 点";
            RuntimeLogService.Warning(
                "空投定位",
                "射线交点超出地图范围",
                $"地图: {mapId}\n估算坐标: X {estimate?.X:0.##} · Z {estimate?.Z:0.##}");
            UpdateAirdropLocatorUi();
            UpdateMarkers();
            ShowToast(_airdropIssue);
            return;
        }

        _airdropEstimate = estimate;
        _airdropIssue = null;
        _airdropLocatorStage = AirdropLocatorStage.Solved;
        RuntimeLogService.Info(
            "空投定位",
            "已计算空投估算位置",
            $"地图: {mapId}\n坐标: X {estimate.X:0.##} · Z {estimate.Z:0.##}\n" +
            $"A 点距离: {estimate.DistanceFromA:0} 米 · B 点距离: {estimate.DistanceFromB:0} 米\n" +
            $"基线: {estimate.BaselineDistance:0} 米 · 交会角: {estimate.CrossingAngleDegrees:0.0}°");
        UpdateAirdropLocatorUi();
        UpdateMarkers();
        ShowToast("空投位置已估算并标记到地图");
    }

    private void UpdateAirdropLocatorUi()
    {
        if (AirdropLocatorButton is null || AirdropLocatorPanel is null) return;
        var active = _airdropLocatorStage != AirdropLocatorStage.Inactive;
        SetToolButton(AirdropLocatorButton, active);
        AirdropLocatorButton.Background = (Brush)FindResource("RaisedBrush");
        AirdropShortcutText.Foreground = active ? (Brush)FindResource("AirdropABrush") : (Brush)FindResource("TextFaintBrush");
        AirdropLocatorPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;

        var accent = (Brush)FindResource("AirdropABrush");
        switch (_airdropLocatorStage)
        {
            case AirdropLocatorStage.WaitingForFirst:
                AirdropStatusTitleText.Text = "等待 A 点截图";
                AirdropStatusDetailText.Text = _airdropIssue ?? "瞄准空投后按 · 截取第一张坐标图";
                break;
            case AirdropLocatorStage.WaitingForSecond:
                accent = (Brush)FindResource(string.IsNullOrWhiteSpace(_airdropIssue) ? "AirdropBBrush" : "DangerBrush");
                AirdropStatusTitleText.Text = string.IsNullOrWhiteSpace(_airdropIssue) ? "A 点已记录 · 等待 B 点" : "B 点无效 · 等待重新截图";
                AirdropStatusDetailText.Text = _airdropIssue ?? $"横向移动至少 {AirdropTriangulationService.MinimumBaselineMeters:0} 米，再次瞄准空投并按 ·";
                break;
            case AirdropLocatorStage.Solved:
                accent = (Brush)FindResource("AirdropEstimateBrush");
                AirdropStatusTitleText.Text = "空投位置已估算";
                AirdropStatusDetailText.Text = _airdropEstimate is null
                    ? "等待估算结果"
                    : $"X {_airdropEstimate.X:0.0} · Z {_airdropEstimate.Z:0.0} · 交会角 {_airdropEstimate.CrossingAngleDegrees:0.0}° · 基线 {_airdropEstimate.BaselineDistance:0} m";
                break;
        }

        AirdropStateDot.Fill = accent;
        AirdropLocatorPanel.BorderBrush = accent;
        AirdropSampleText.Text = $"A {FormatAirdropSample(_airdropFirstBearing)}   B {FormatAirdropSample(_airdropSecondBearing)}";
    }

    private static string FormatAirdropSample(AirdropBearingSample? sample) => sample is null
        ? "—"
        : $"X{sample.X:0.0} Z{sample.Z:0.0} @{sample.YawDegrees:0}°";

    private void UpdateMarkers()
    {
        if (!IsLoaded || MarkerCanvas is null || DynamicMarkerCanvas is null || MapTransformHost.ActualWidth < 20 || MapTransformHost.ActualHeight < 20) return;

        var staticMarkers = GetStaticVisibleMarkers();
        var dynamicMarkers = GetDynamicVisibleMarkers();
        var staticChanged = RefreshMarkerLayer(
            MarkerCanvas,
            _staticRenderedMarkers,
            staticMarkers,
            ref _staticMarkerVisualsInitialized,
            ref _staticMarkerVisualSignature);
        var dynamicChanged = RefreshMarkerLayer(
            DynamicMarkerCanvas,
            _dynamicRenderedMarkers,
            dynamicMarkers,
            ref _dynamicMarkerVisualsInitialized,
            ref _dynamicMarkerVisualSignature);

        if (staticChanged || dynamicChanged) RepositionMarkers();
        else QueueMarkerReposition();
        var visibleMarkers = staticMarkers.Concat(dynamicMarkers).ToArray();
        UpdateMiniMap(visibleMarkers);
    }

    private MapMarker[] GetVisibleMarkers()
        => GetStaticVisibleMarkers().Concat(GetDynamicVisibleMarkers()).ToArray();

    private MapMarker[] GetStaticVisibleMarkers() => (_viewModel.SelectedMap?.Markers ?? [])
        .Concat(GetSelectedTaskMarkers())
        .Where(IsMarkerVisible)
        .ToArray();

    private MapMarker[] GetDynamicVisibleMarkers()
    {
        IEnumerable<MapMarker> markers = [];
#if DEBUG
        markers = markers.Append(PlayerMarkerPreview);
#endif
        if (_viewModel.PlayerMarker is { } playerMarker) markers = markers.Append(playerMarker);
        return markers
            .Concat(GetAirdropMarkers())
            .Concat(GetLanPeerMarkers())
            .Concat(GetBtrPredictionMarkers())
            .Where(IsMarkerOnActiveLayer)
            .ToArray();
    }

    private IEnumerable<MapMarker> GetAirdropMarkers()
    {
        var overlay = GetAirdropMapOverlay();
        var bounds = _viewModel.SelectedMap?.WorldBounds;
        if (overlay is null || bounds is null) yield break;

        if (_airdropFirstBearing is { } first && overlay.FirstRay is { } firstRay)
        {
            yield return new MapMarker
            {
                Type = "airdrop-a",
                Label = "空投取向 A",
                ToolTipText = $"A 点\nX {first.X:0.##} · Z {first.Z:0.##}\n视角 {first.YawDegrees:0.##}°",
                X = firstRay.StartX,
                Y = firstRay.StartY,
                ShowLabel = true,
                HeadingDegrees = bounds.Value.ProjectHeading(first.YawDegrees)
            };
        }

        if (_airdropSecondBearing is { } second && overlay.SecondRay is { } secondRay)
        {
            yield return new MapMarker
            {
                Type = "airdrop-b",
                Label = "空投取向 B",
                ToolTipText = $"B 点\nX {second.X:0.##} · Z {second.Z:0.##}\n视角 {second.YawDegrees:0.##}°",
                X = secondRay.StartX,
                Y = secondRay.StartY,
                ShowLabel = true,
                HeadingDegrees = bounds.Value.ProjectHeading(second.YawDegrees)
            };
        }

        if (_airdropEstimate is { } estimate && overlay.EstimateX is { } estimateX && overlay.EstimateY is { } estimateY)
        {
            yield return new MapMarker
            {
                Type = "airdrop-estimate",
                Label = "空投估算位置",
                ToolTipText = $"空投估算位置\nX {estimate.X:0.##} · Z {estimate.Z:0.##}\n交会角 {estimate.CrossingAngleDegrees:0.0}°",
                X = estimateX,
                Y = estimateY,
                ShowLabel = true
            };
        }
    }

    private AirdropMapOverlay? GetAirdropMapOverlay()
    {
        var map = _viewModel.SelectedMap;
        if (_airdropLocatorStage == AirdropLocatorStage.Inactive || map?.WorldBounds is not { } bounds) return null;
        if (_airdropFirstBearing is { } first && !string.Equals(first.MapId, map.Id, StringComparison.OrdinalIgnoreCase)) return null;

        AirdropMapRay? firstRay = null;
        AirdropMapRay? secondRay = null;
        if (_airdropFirstBearing is { } firstBearing)
            AirdropTriangulationService.TryBuildMapRay(bounds, firstBearing, out firstRay);
        if (_airdropSecondBearing is { } secondBearing &&
            string.Equals(secondBearing.MapId, map.Id, StringComparison.OrdinalIgnoreCase))
            AirdropTriangulationService.TryBuildMapRay(bounds, secondBearing, out secondRay);

        double? estimateX = null;
        double? estimateY = null;
        if (_airdropEstimate is { } estimate && bounds.TryProject(estimate.X, estimate.Z, out var projectedX, out var projectedY))
        {
            estimateX = projectedX;
            estimateY = projectedY;
        }

        return firstRay is null && secondRay is null && estimateX is null
            ? null
            : new AirdropMapOverlay(firstRay, secondRay, estimateX, estimateY);
    }

    private bool RefreshMarkerLayer(
        Canvas canvas,
        List<RenderedMarker> renderedMarkers,
        IReadOnlyList<MapMarker> markers,
        ref bool initialized,
        ref string? currentSignature)
    {
        var signature = BuildMarkerVisualSignature(markers);
        if (initialized && string.Equals(signature, currentSignature, StringComparison.Ordinal)) return false;

        currentSignature = signature;
        canvas.Children.Clear();
        renderedMarkers.Clear();
        foreach (var marker in markers)
        {
            var markerHost = CreateMarker(marker);
            canvas.Children.Add(markerHost);
            renderedMarkers.Add(new RenderedMarker(markerHost, marker));
        }

        initialized = true;
        return true;
    }

    private string BuildMarkerVisualSignature(IReadOnlyList<MapMarker> markers)
    {
        var builder = new StringBuilder((_viewModel.SelectedMap?.Id?.Length ?? 0) + markers.Count * 64);
        builder.Append(_viewModel.SelectedMap?.Id).Append('|');
        foreach (var marker in markers)
        {
            builder.Append(marker.Type).Append('\u001f')
                .Append(marker.Label).Append('\u001f')
                .Append(marker.ToolTipText).Append('\u001f')
                .Append(marker.PreviewImageId).Append('\u001f')
                .Append(marker.PreviewImageUrl).Append('\u001f')
                .Append(marker.X.ToString("R", CultureInfo.InvariantCulture)).Append('\u001f')
                .Append(marker.Y.ToString("R", CultureInfo.InvariantCulture)).Append('\u001f')
                .Append(marker.WorldX?.ToString("R", CultureInfo.InvariantCulture)).Append('\u001f')
                .Append(marker.WorldZ?.ToString("R", CultureInfo.InvariantCulture)).Append('\u001f')
                .Append(marker.HeadingDegrees?.ToString("R", CultureInfo.InvariantCulture)).Append('\u001f')
                .Append(marker.ColorHex).Append('\u001f')
                .Append(marker.ShowLabel ? '1' : '0').Append('\u001e');
        }
        return builder.ToString();
    }

    private void QueueMarkerReposition()
    {
        if (_markerRepositionQueued || _isWindowClosing) return;
        _markerRepositionQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _markerRepositionQueued = false;
            if (_isWindowClosing) return;
            RepositionMarkers();
        });
    }

    private void RepositionMarkers()
    {
        if (!IsLoaded || MarkerCanvas is null || DynamicMarkerCanvas is null || MapTransformHost.ActualWidth < 20 || MapTransformHost.ActualHeight < 20) return;
        if (!_staticMarkerVisualsInitialized || !_dynamicMarkerVisualsInitialized)
        {
            UpdateMarkers();
            return;
        }

        var imageBounds = GetImageDisplayBounds();
        var markerProjection = GetActiveMarkerProjection();
        RepositionAirdropRays(imageBounds, markerProjection);
        foreach (var rendered in EnumerateRenderedMarkers())
        {
            if (rendered.Element.RenderTransform is ScaleTransform scale)
            {
                scale.ScaleX = 1 / _zoom;
                scale.ScaleY = 1 / _zoom;
            }
            // The canvas is scaled with the map while marker visuals are inversely scaled.
            // Compensate the anchor by the same factor so the center remains on its map coordinate.
            var markerOffset = GetMarkerDotSize(rendered.Marker) / 2d / _zoom;
            var projected = ProjectMarker(rendered.Marker, markerProjection);
            Canvas.SetLeft(rendered.Element, imageBounds.X + projected.X * imageBounds.Width - markerOffset);
            Canvas.SetTop(rendered.Element, imageBounds.Y + projected.Y * imageBounds.Height - markerOffset);
        }

        ArrangeMarkerLabels(imageBounds, markerProjection);
    }

    private void RepositionAirdropRays(Rect imageBounds, MapPointProjection markerProjection)
    {
        if (AirdropRayCanvas is null) return;
        AirdropRayCanvas.Children.Clear();
        var overlay = GetAirdropMapOverlay();
        if (overlay is null) return;

        if (overlay.FirstRay is { } first)
            AddAirdropRay(first, imageBounds, markerProjection, (Brush)FindResource("AirdropABrush"), [5, 3]);
        if (overlay.SecondRay is { } second)
            AddAirdropRay(second, imageBounds, markerProjection, (Brush)FindResource("AirdropBBrush"), [3, 3]);
    }

    private void AddAirdropRay(AirdropMapRay ray, Rect imageBounds, MapPointProjection markerProjection, Brush brush, DoubleCollection dash)
    {
        var start = markerProjection.Transform(ray.StartX, ray.StartY);
        var end = markerProjection.Transform(ray.EndX, ray.EndY);
        var x1 = imageBounds.X + start.X * imageBounds.Width;
        var y1 = imageBounds.Y + start.Y * imageBounds.Height;
        var x2 = imageBounds.X + end.X * imageBounds.Width;
        var y2 = imageBounds.Y + end.Y * imageBounds.Height;
        AirdropRayCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 6 / _zoom,
            Opacity = .16,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        AirdropRayCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 1.8 / _zoom,
            StrokeDashArray = dash,
            Opacity = .95,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
    }

    private static double GetMarkerDotSize(MapMarker marker) => marker.Type switch
    {
        "player" or "player-stale" or "player-preview" => 24,
        "key-room" => 16,
        "switch" => 15,
        "season-document" => 15,
        "btr" => 20,
        "airdrop-estimate" => 22,
        "airdrop-a" or "airdrop-b" => 18,
        _ => marker.ShowLabel ? 14 : 8
    };

    private void ArrangeMarkerLabels(Rect imageBounds, MapPointProjection markerProjection)
    {
        var entries = new List<(RenderedMarker Rendered, MarkerLabelLayout Layout)>();
        foreach (var rendered in EnumerateRenderedMarkers())
            if (rendered.Element.Tag is MarkerLabelLayout layout)
                entries.Add((rendered, layout));
        if (entries.Count == 0) return;

        var occupied = new RectSpatialIndex();
        var imageLeft = imageBounds.Left * _zoom;
        var imageTop = imageBounds.Top * _zoom;
        var imageRight = imageBounds.Right * _zoom;
        var imageBottom = imageBounds.Bottom * _zoom;

        foreach (var entry in entries.OrderBy(item => ProjectMarker(item.Rendered.Marker, markerProjection).Y)
                     .ThenBy(item => ProjectMarker(item.Rendered.Marker, markerProjection).X))
        {
            var marker = entry.Rendered.Marker;
            var projected = ProjectMarker(marker, markerProjection);
            var layout = entry.Layout;
            layout.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = Math.Max(1, layout.Label.DesiredSize.Width);
            var labelHeight = Math.Max(1, layout.Label.DesiredSize.Height);
            var pointX = (imageBounds.X + projected.X * imageBounds.Width) * _zoom;
            var pointY = (imageBounds.Y + projected.Y * imageBounds.Height) * _zoom;
            var hostX = pointX - layout.DotSize / 2;
            var hostY = pointY - layout.DotSize / 2;
            var preferredLeft = projected.X >= .7;
            bool[] sides = projected.X >= .7 ? [true] : projected.X <= .3 ? [false] : [preferredLeft, !preferredLeft];
            var step = Math.Max(21, labelHeight + 4);

            Rect? selectedBounds = null;
            var selectedLeft = preferredLeft;
            var selectedOffset = 0d;
            for (var level = 0; level <= 14 && selectedBounds is null; level++)
            {
                double[] offsets = level == 0 ? [0] : [-step * level, step * level];
                foreach (var verticalOffset in offsets)
                {
                    foreach (var placeLeft in sides)
                    {
                        var labelX = placeLeft
                            ? hostX - labelWidth - 6
                            : hostX + layout.DotSize + 6;
                        var labelY = hostY + layout.BaseTop + verticalOffset;
                        var bounds = new Rect(labelX, labelY, labelWidth, labelHeight);
                        if (bounds.Left < imageLeft || bounds.Right > imageRight || bounds.Top < imageTop || bounds.Bottom > imageBottom) continue;

                        var paddedBounds = bounds;
                        paddedBounds.Inflate(3, 2);
                        if (occupied.Intersects(paddedBounds)) continue;

                        selectedBounds = paddedBounds;
                        selectedLeft = placeLeft;
                        selectedOffset = verticalOffset;
                        break;
                    }
                    if (selectedBounds is not null) break;
                }
            }

            if (selectedBounds is null)
            {
                var fallbackX = preferredLeft
                    ? hostX - labelWidth - 6
                    : hostX + layout.DotSize + 6;
                var fallback = new Rect(fallbackX, hostY + layout.BaseTop, labelWidth, labelHeight);
                fallback.Inflate(3, 2);
                selectedBounds = fallback;
            }

            ApplyMarkerLabelPlacement(layout, selectedLeft, selectedOffset, labelHeight);
            occupied.Add(selectedBounds.Value);
        }
    }

    private IEnumerable<RenderedMarker> EnumerateRenderedMarkers() =>
        _staticRenderedMarkers.Concat(_dynamicRenderedMarkers);

    private static void ApplyMarkerLabelPlacement(MarkerLabelLayout layout, bool placeLeft, double verticalOffset, double labelHeight)
    {
        Canvas.SetLeft(layout.Label, double.NaN);
        Canvas.SetRight(layout.Label, double.NaN);
        if (placeLeft)
            Canvas.SetRight(layout.Label, layout.DotSize + 6);
        else
            Canvas.SetLeft(layout.Label, layout.DotSize + 6);
        Canvas.SetTop(layout.Label, layout.BaseTop + verticalOffset);

        layout.Leader.X1 = layout.DotSize / 2;
        layout.Leader.Y1 = layout.DotSize / 2;
        layout.Leader.X2 = placeLeft ? -6 : layout.DotSize + 6;
        layout.Leader.Y2 = layout.BaseTop + verticalOffset + labelHeight / 2;
        layout.Leader.Visibility = Math.Abs(verticalOffset) > .5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMiniMap(IReadOnlyList<MapMarker> visibleMarkers)
    {
        if (_miniMapWindow is not { IsVisible: true }) return;
        _miniMapWindow.UpdateMap(
            GetActiveBaseMapImage(),
            MapLayerImage.Source,
            _viewModel.CurrentMapName,
            _activeMapLayer?.Name,
            visibleMarkers,
            GetAirdropMapOverlay(),
            GetActiveMarkerProjection(),
            _activeMapStyleMapId,
            _activeMapStyle);
    }

    private (double X, double Y) ProjectMarker(MapMarker marker, MapPointProjection fallbackProjection) =>
        MapBaseProjectionService.ProjectMarker(
            marker,
            _activeMapStyleMapId,
            _activeMapStyle,
            fallbackProjection);

    private MapPointProjection GetActiveMarkerProjection() =>
        MapBaseProjectionService.Resolve(_activeMapStyleMapId, _activeMapStyle);

    private Rect GetImageDisplayBounds()
    {
        var canvasWidth = MapTransformHost.ActualWidth;
        var canvasHeight = MapTransformHost.ActualHeight;
        if (GetActiveBaseMapImage() is not BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } source)
            return new Rect(0, 0, canvasWidth, canvasHeight);

        var scale = Math.Min(canvasWidth / source.PixelWidth, canvasHeight / source.PixelHeight);
        var imageWidth = source.PixelWidth * scale;
        var imageHeight = source.PixelHeight * scale;
        return new Rect((canvasWidth - imageWidth) / 2, (canvasHeight - imageHeight) / 2, imageWidth, imageHeight);
    }

    private ImageSource? GetActiveBaseMapImage() =>
        MapBaseVariantImage.Visibility == Visibility.Visible && MapBaseVariantImage.Source is not null
            ? MapBaseVariantImage.Source
            : _viewModel.CurrentMapImage;

    private void BeginMapTransition(MapDefinition? previousMap)
    {
        var version = ++_mapTransitionVersion;
        var previousImage = previousMap?.ImageSource;
        if (!MotionEnabled || previousImage is null || ReferenceEquals(previousMap, _viewModel.SelectedMap))
        {
            MapTransitionImage.BeginAnimation(OpacityProperty, null);
            MapTransitionImage.Source = null;
            MapTransitionImage.Opacity = 0;
            MapTransitionImage.Visibility = Visibility.Collapsed;
            return;
        }

        MapTransitionImage.BeginAnimation(OpacityProperty, null);
        MapTransitionImage.Source = previousImage;
        MapTransitionImage.Opacity = .9;
        MapTransitionImage.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (version != _mapTransitionVersion || MapTransitionImage.Visibility != Visibility.Visible) return;

            var fade = new DoubleAnimation(.9, 0, new Duration(TimeSpan.FromMilliseconds(235)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (_, _) =>
            {
                if (version != _mapTransitionVersion) return;
                MapTransitionImage.BeginAnimation(OpacityProperty, null);
                MapTransitionImage.Source = null;
                MapTransitionImage.Opacity = 0;
                MapTransitionImage.Visibility = Visibility.Collapsed;
            };
            MapTransitionImage.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        });
    }

    private bool IsMarkerVisible(MapMarker marker)
    {
        var typeVisible = marker.Type switch
        {
            "extract" or "transit" => ExtractPointDisplayCheck.IsChecked == true,
            "task" => TaskPointDisplayCheck.IsChecked == true,
            "key-room" => KeyRoomPointDisplayCheck.IsChecked == true,
            "switch" => SwitchPointDisplayCheck.IsChecked == true,
            "season-document" => SeasonDocumentPointDisplayCheck.IsChecked == true,
            "btr" => BtrPointDisplayCheck.IsChecked == true,
            _ => true
        };
        return typeVisible && IsMarkerOnActiveLayer(marker);
    }

    private bool IsMarkerOnActiveLayer(MapMarker marker)
    {
        IReadOnlyList<MapLayerDefinition>? layers = null;
        if (_viewModel.SelectedMap is { } map)
            layers = GetAvailableMapLayers(map.Id);
        return MapLayerVisibilityService.IsVisible(marker, _activeMapLayer, layers);
    }

    private IEnumerable<MapMarker> GetBtrPredictionMarkers()
    {
        var map = _viewModel.SelectedMap;
        var snapshot = _lastRaidSnapshot;
        if (BtrPointDisplayCheck.IsChecked != true || map is null || snapshot?.GameStartAt is null || snapshot.RaidEndAt is not null ||
            !string.Equals(snapshot.MapKey, map.Id, StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            return _btrPrediction.BuildMarkers(map, snapshot.GameStartAt, DateTimeOffset.Now);
        }
        catch (Exception)
        {
            // The predictor is an optional enhancement; a missing/incompatible
            // original runtime must never take down the map workspace.
            return [];
        }
    }

    private IEnumerable<MapMarker> GetLanPeerMarkers()
    {
        var map = _viewModel.SelectedMap;
        if (_teamSyncFeature is null || map?.WorldBounds is not { } bounds) return [];

        return _teamSyncFeature.GetPeerPositions(map.Id)
            .Select(peer => bounds.TryProject(peer.WorldX, peer.WorldZ, out var x, out var y)
                ? new MapMarker
                {
                    Type = "peer",
                    Label = peer.DisplayName,
                    X = x,
                    Y = y,
                    ShowLabel = true,
                    HeadingDegrees = bounds.ProjectHeading(peer.YawDegrees),
                    ColorHex = peer.Color,
                    WorldX = peer.WorldX,
                    WorldHeight = peer.WorldHeight,
                    WorldZ = peer.WorldZ
                }
                : null)
            .Where(marker => marker is not null)
            .Cast<MapMarker>();
    }

    private FrameworkElement CreateMarker(MapMarker marker)
    {
        var isPlayer = marker.Type is "player" or "player-stale" or "player-preview";
        var isKeyRoom = marker.Type == "key-room";
        var isSwitch = marker.Type == "switch";
        var isSeasonDocument = marker.Type == "season-document";
        var isBtr = marker.Type == "btr";
        var isAirdropBearing = marker.Type is "airdrop-a" or "airdrop-b";
        var isAirdropEstimate = marker.Type == "airdrop-estimate";
        var isEmphasized = isPlayer || isBtr || isAirdropBearing || isAirdropEstimate;
        var brush = marker.Type switch
        {
            "player" or "player-preview" => (SolidColorBrush)FindResource("PlayerBrush"),
            "player-stale" => (SolidColorBrush)FindResource("StalePlayerBrush"),
            "task" => (SolidColorBrush)FindResource("TaskBrush"),
            "extract" => (SolidColorBrush)FindResource("GreenBrush"),
            "transit" => (SolidColorBrush)FindResource("AmberBrush"),
            "key-room" => (SolidColorBrush)FindResource("KeyRoomBrush"),
            "switch" => (SolidColorBrush)FindResource("SwitchBrush"),
            "season-document" => (SolidColorBrush)FindResource("SeasonDocumentBrush"),
            "airdrop-a" => (SolidColorBrush)FindResource("AirdropABrush"),
            "airdrop-b" => (SolidColorBrush)FindResource("AirdropBBrush"),
            "airdrop-estimate" => (SolidColorBrush)FindResource("AirdropEstimateBrush"),
            "btr" => ResolveMarkerBrush(marker.ColorHex, "AmberBrush"),
            "peer" => ResolveMarkerBrush(marker.ColorHex, "AmberBrush"),
            _ => (SolidColorBrush)FindResource("TextDimBrush")
        };
        var dotSize = GetMarkerDotSize(marker);

        var dot = new Border
        {
            Width = dotSize,
            Height = dotSize,
            Background = new SolidColorBrush(Color.FromArgb(isEmphasized ? (byte)238 : marker.ShowLabel ? (byte)80 : (byte)150, brush.Color.R, brush.Color.G, brush.Color.B)),
            BorderBrush = brush,
            BorderThickness = new Thickness(isPlayer || isAirdropEstimate ? 2.5 : isBtr || isAirdropBearing || marker.ShowLabel ? 2 : isKeyRoom ? 1.5 : 1),
            CornerRadius = new CornerRadius(dotSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = isEmphasized
                ? new System.Windows.Media.Effects.DropShadowEffect { Color = brush.Color, BlurRadius = 9, ShadowDepth = 0, Opacity = .8 }
                : null
        };
        MarkerToolTipFactory.Attach(dot, marker);

        if (marker.HeadingDegrees is { } heading)
        {
            dot.Child = new Viewbox
            {
                Width = isPlayer ? 14 : isAirdropBearing ? 10 : 9,
                Height = isPlayer ? 16 : isAirdropBearing ? 12 : 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path
                {
                    Data = DirectionArrowGeometry,
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform
                },
                RenderTransformOrigin = new Point(.5, .5),
                RenderTransform = new RotateTransform(heading)
            };
        }
        else if (isBtr)
        {
            dot.Child = new TextBlock
            {
                Text = "B",
                Foreground = Brushes.Black,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (isAirdropEstimate)
        {
            dot.Child = new TextBlock
            {
                Text = "×",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };
        }
        else if (isKeyRoom)
        {
            dot.Child = new Viewbox
            {
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = new System.Windows.Shapes.Path
                {
                    Data = LockMarkerGeometry,
                    Fill = Brushes.Transparent,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.Uniform
                }
            };
        }
        else if (isSwitch)
        {
            dot.Child = new TextBlock
            {
                Text = "⚡",
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (isSeasonDocument)
        {
            dot.Child = new TextBlock
            {
                Text = "文",
                Foreground = Brushes.White,
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (!marker.ShowLabel)
        {
            dot.RenderTransformOrigin = new Point(0, 0);
            dot.RenderTransform = new ScaleTransform(1 / _zoom, 1 / _zoom);
            return dot;
        }

        var label = new Border
        {
            Padding = new Thickness(5, 3, 5, 3),
            Background = new SolidColorBrush(Color.FromArgb(232, 18, 24, 22)),
            BorderBrush = isPlayer || isBtr || isAirdropBearing || isAirdropEstimate || marker.Type == "task" ? brush : (Brush)FindResource("LineBrightBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock { Text = marker.Label, Foreground = (Brush)FindResource("TextBrush"), FontSize = 10, FontFamily = new FontFamily("Microsoft YaHei UI") }
        };
        MarkerToolTipFactory.Attach(label, marker);

        var markerHost = new Canvas { Width = dotSize, Height = dotSize, ClipToBounds = false };
        var leader = new Line
        {
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            Opacity = .6,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Canvas.SetLeft(dot, 0);
        Canvas.SetTop(dot, 0);
        var labelTop = isPlayer ? 1d : -4d;
        Canvas.SetTop(label, labelTop);
        if (marker.X >= .7)
            Canvas.SetRight(label, dotSize + 6);
        else
            Canvas.SetLeft(label, dotSize + 6);
        markerHost.Children.Add(leader);
        markerHost.Children.Add(dot);
        markerHost.Children.Add(label);
        markerHost.Tag = new MarkerLabelLayout(markerHost, label, leader, dotSize, labelTop);
        markerHost.RenderTransformOrigin = new Point(0, 0);
        markerHost.RenderTransform = new ScaleTransform(1 / _zoom, 1 / _zoom);
        return markerHost;
    }

    private SolidColorBrush ResolveMarkerBrush(string? colorHex, string fallbackResourceKey)
    {
        var fallback = (SolidColorBrush)FindResource(fallbackResourceKey);
        if (string.IsNullOrWhiteSpace(colorHex)) return fallback;
        try
        {
            return ColorConverter.ConvertFromString(colorHex) is Color color
                ? new SolidColorBrush(color)
                : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private void MapViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetMapZoom(_zoom * (e.Delta > 0 ? 1.18 : 1 / 1.18));
        e.Handled = true;
    }

    private void MapViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(MapViewport);
        MapViewport.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
    }

    private void MapViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var point = e.GetPosition(MapViewport);
        MapTranslateTransform.X += point.X - _panStart.X;
        MapTranslateTransform.Y += point.Y - _panStart.Y;
        _panStart = point;
    }

    private void MapViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();
    private void MapViewport_LostMouseCapture(object sender, MouseEventArgs e) => EndPan();

    private void EndPan()
    {
        _isPanning = false;
        if (MapViewport.IsMouseCaptured) MapViewport.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(.15);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(-.15);
    private void ResetMap_Click(object sender, RoutedEventArgs e) => ResetMapTransform();

    private void ZoomBy(double delta) => SetMapZoom(_zoom + delta);

    private void SetMapZoom(double value)
    {
        _zoom = Math.Clamp(value, MinimumMapZoom, MaximumMapZoom);
        MapScaleTransform.ScaleX = _zoom;
        MapScaleTransform.ScaleY = _zoom;
        QueueMarkerReposition();
    }

    private void ResetMapTransform()
    {
        _zoom = 1;
        MapScaleTransform.ScaleX = 1;
        MapScaleTransform.ScaleY = 1;
        MapTranslateTransform.X = 0;
        MapTranslateTransform.Y = 0;
        QueueMarkerReposition();
    }

    private async void ListeningButton_Click(object sender, RoutedEventArgs e)
    {
        _isListening = !_isListening;
        if (_isListening)
        {
            _lastAppliedRaidMapSignature = null;
            _raidMonitor.RequestScreenshotRescan(restartWatcher: true);
            RuntimeLogService.Info(
                "监听",
                "开始监听本机游戏数据",
                $"自动刷新: {(_isAutoRefreshEnabled ? "开启" : "关闭")}\n" +
                $"刷新间隔: {_autoRefreshTimer.Interval.TotalSeconds:0.##} 秒\n" +
                $"截图目录: {_desktopPreferences.ScreenshotDirectory ?? "未配置"}\n" +
                $"游戏日志目录: {_desktopPreferences.GameLogDirectory ?? "未配置"}");
            UpdateListeningControls();
            await RefreshLocalRaidDataAsync();
            if (_isAutoRefreshEnabled) _autoRefreshTimer.Start();
            ShowToast("已开始本机日志与截图监听");
        }
        else
        {
            RuntimeLogService.Info("监听", "停止监听本机游戏数据", $"最后地图: {_viewModel.CurrentMapName}\n最后更新: {_viewModel.LastUpdate}");
            _autoRefreshTimer.Stop();
            UpdateListeningControls();
            ShowToast("已停止本机监听");
        }
    }

    private async void AutoRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _isAutoRefreshEnabled = !_isAutoRefreshEnabled;
        RuntimeLogService.Info(
            "监听",
            _isAutoRefreshEnabled ? "开启自动刷新" : "暂停自动刷新",
            $"监听状态: {(_isListening ? "监听中" : "未监听")}\n刷新间隔: {_autoRefreshTimer.Interval.TotalSeconds:0.##} 秒");
        if (_isListening && _isAutoRefreshEnabled)
        {
            _raidMonitor.RequestScreenshotRescan();
            _autoRefreshTimer.Start();
            await RefreshLocalRaidDataAsync();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }

        UpdateListeningControls();
        ShowToast(_isAutoRefreshEnabled ? "自动刷新已开启" : "自动刷新已暂停");
    }

    private void UpdateListeningControls()
    {
        SetToolButton(ListeningButton, _isListening);
        SetToolButton(AutoRefreshButton, _isListening && _isAutoRefreshEnabled);
        AutoRefreshButton.IsEnabled = _isListening;
        AutoRefreshButton.ToolTip = _isListening
            ? "自动读取新的游戏日志与坐标截图"
            : "请先开始监听";
        ListeningButton.Content = _isListening ? "停止监听" : "开始监听";
        ListeningStateDot.Fill = _isListening ? (Brush)FindResource("GreenBrush") : (Brush)FindResource("TextFaintBrush");
        ListeningStateText.Text = _isListening ? "监听中" : "未监听";
        ListeningStateText.Foreground = _isListening ? (Brush)FindResource("GreenBrush") : (Brush)FindResource("TextFaintBrush");

        if (_isListening) return;
        MonitorStatusText.Text = _requiresInitialPathSetup ? "运行状态：未配置路径" : "运行状态：等待开始";
    }

    private void MapNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkspacePage(WorkspacePage.Map);
        MapList.Focus();
    }
    private void MarketNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void InGamePriceNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void TaskItemsNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void MemoNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void TaskTrackingNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void ScreenFilterNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void TeamSyncNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void MobileMapNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void UtilitiesNavigation_Click(object sender, RoutedEventArgs e) => NavigateFeature(sender);
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Settings);

    private void NavigateFeature(object sender)
    {
        var registration = _featurePages.FirstOrDefault(item => ReferenceEquals(item.RailButton, sender));
        if (registration is not null) ShowWorkspacePage(registration.Page);
    }

    void IFeatureHost.Navigate(string route)
    {
        var registration = FindFeaturePage(route);
        if (registration is null)
        {
            RuntimeLogService.Warning("组件", "组件请求了未知页面", $"路由: {route}");
            return;
        }
        ShowWorkspacePage(registration.Page);
    }

    bool IFeatureHost.NavigateBack() => NavigateBack();

    string IFeatureHost.DataDirectory => Services.ApplicationIdentity.ApplicationDataDirectory;

    string IFeatureHost.CurrentGameMode => _taskTrackingFeature?.SelectedTaskMode ?? "pve";

    string IFeatureHost.CurrentMarketMode => _marketMode;

    CancellationToken IFeatureHost.ShutdownToken => _lifetimeCts.Token;

    void IFeatureHost.ShowNotification(string message) => ShowToast(message);

    void IFeatureHost.WriteLog(FeatureLogLevel level, string category, string message, string? details)
    {
        switch (level)
        {
            case FeatureLogLevel.Trace:
                RuntimeLogService.Trace(category, message, details);
                break;
            case FeatureLogLevel.Warning:
                RuntimeLogService.Warning(category, message, details);
                break;
            case FeatureLogLevel.Error:
                RuntimeLogService.Error(category, message, new InvalidOperationException(details ?? message));
                break;
            default:
                RuntimeLogService.Info(category, message, details);
                break;
        }
    }

    IReadOnlyDictionary<string, FeatureMarketItem> IFeatureHost.GetMarketItems(string mode) =>
        MarketPriceService.GetItemIndex(mode).ToDictionary(
            pair => pair.Key,
            pair => ToFeatureMarketItem(pair.Value),
            StringComparer.Ordinal);

    FeatureMarketSnapshot IFeatureHost.GetMarketSnapshot(string mode)
    {
        var items = _marketCatalog.GetItems(mode).Select(ToFeatureMarketItem).ToArray();
        return new FeatureMarketSnapshot(
            items,
            _marketCatalog.UpdatedAt,
            _marketCatalog.Source,
            _marketCatalog.ErrorMessage,
            _marketCatalog.IsStale,
            _marketCatalog.IsAvailable);
    }

    void IFeatureHost.SetMarketMode(string mode) =>
        _marketMode = mode?.Trim().ToLowerInvariant() switch
        {
            "pve" => "pve",
            "pvp-season" or "season" => "pvp-season",
            _ => "pvp"
        };

    async Task IFeatureHost.EnsureMarketFreshAsync(CancellationToken cancellationToken)
    {
        ApplyFeatureMarketCatalog(await MarketPriceService.EnsureFreshAsync(cancellationToken));
    }

    async Task IFeatureHost.RefreshMarketAsync(CancellationToken cancellationToken)
    {
        ApplyFeatureMarketCatalog(await MarketPriceService.RefreshAsync(cancellationToken));
    }

    private void ApplyFeatureMarketCatalog(MarketCatalog catalog)
    {
        _marketCatalog = catalog;
        MarketDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private static FeatureMarketItem ToFeatureMarketItem(MarketItem item) => new(
        item.Id,
        item.NameZh,
        item.ShortNameZh,
        item.Name,
        item.ShortName,
        item.Avg24hPrice,
        item.Low24hPrice,
        item.High24hPrice,
        item.LastLowPrice,
        item.FleaPrice,
        item.BestTrader is { } trader ? new FeatureTraderOffer(trader.Price, trader.VendorName) : null,
        item.SellFor.Select(offer => new FeatureMarketSellOffer(offer.Price, offer.Source, offer.VendorName)).ToArray(),
        item.IconLink,
        item.GridImageLink,
        item.Types ?? [],
        item.BasePrice,
        item.BestTraderBuy is { } purchase ? new FeatureTraderOffer(purchase.Price, purchase.VendorName, purchase.MinTraderLevel) : null,
        item.Width,
        item.Height);

    FeatureTaskTrackerSnapshot IFeatureHost.GetTaskItems(
        string mode,
        string query,
        bool includeTasks,
        bool includeHideout,
        bool showCompleted) =>
        ToFeatureTaskTrackerSnapshot(TaskItemTrackerService.Load(mode, query, includeTasks, includeHideout, showCompleted));

    async Task IFeatureHost.EnsureTaskItemsFreshAsync(CancellationToken cancellationToken) =>
        await TaskItemTrackerService.EnsureFreshAsync(cancellationToken);

    async Task IFeatureHost.RefreshTaskItemsAsync(CancellationToken cancellationToken) =>
        _ = await TaskItemTrackerService.RefreshAsync(cancellationToken);

    void IFeatureHost.SetTaskItemCount(string mode, string itemId, int have) =>
        TaskItemTrackerService.SetItemCount(mode, itemId, have);

    void IFeatureHost.SetTaskItemSourceCompleted(string mode, string sourceKey, bool completed) =>
        TaskItemTrackerService.SetSourceCompleted(mode, sourceKey, completed);

    IReadOnlyList<FeatureHideoutStation> IFeatureHost.GetHideoutStations(string mode) =>
        TaskItemTrackerService.GetHideoutStations(mode)
            .Select(station => new FeatureHideoutStation(station.Id, station.Name, station.CurrentLevel, station.MaxLevel))
            .ToArray();

    void IFeatureHost.SetHideoutLevels(string mode, IReadOnlyDictionary<string, int> levels) =>
        TaskItemTrackerService.SetHideoutLevels(mode, levels);

    void IFeatureHost.ApplyTaskTrackingPinSelection(bool enabled, IReadOnlyList<string> taskNames) =>
        ApplyTaskTrackingPinSelection(enabled, taskNames);

    FeatureSharedLocation IFeatureHost.GetSharedLocation()
    {
        var map = _viewModel.SelectedMap;
        var coordinate = _viewModel.GetShareableCoordinate(DateTimeOffset.Now);
        return new FeatureSharedLocation(
            map?.Id,
            map?.Name,
            coordinate?.X,
            coordinate?.Y,
            coordinate?.Z,
            coordinate?.YawDegrees);
    }

    int IFeatureHost.EnsureMobileMapServerRunning()
    {
        if (_mobileMapFeature is not null && _mobileMapFeature.EnsureRunning()) return _mobileMapFeature.Port;
        ShowToast("手机地图组件不可用或启动失败");
        return 0;
    }

    string? IFeatureHost.HandleTeamSyncRequest(string requestJson, string remoteAddress) =>
        _teamSyncFeature?.HandleSyncRequest(requestJson, remoteAddress);

    async Task IFeatureHost.StopHostedTeamSyncAsync()
    {
        if (_teamSyncFeature is not null) await _teamSyncFeature.StopHostingAsync();
    }

    FeatureMobileMapSnapshot IFeatureHost.GetMobileMapSnapshot()
    {
        var map = _viewModel.SelectedMap;
        if (map is null)
        {
            return new FeatureMobileMapSnapshot(
                null,
                null,
                "2d",
                null,
                "地面",
                null,
                null,
                false,
                _isListening,
                false,
                false,
                null,
                []);
        }

        var webMap = WebMapDefinition.Empty;
        var satelliteActive = string.Equals(_activeMapStyleMapId, map.Id, StringComparison.OrdinalIgnoreCase) &&
                              MapBaseProjectionService.IsSatelliteMapStyle(_activeMapStyle) &&
                              MapBaseProjectionService.TryGetWebMap(map.Id, out webMap);
        var basePath = satelliteActive ? GetWebMapImagePath(webMap) : map.ImageFilePath;
        var surfaceName = satelliteActive
            ? webMap.SurfaceName ?? "地面"
            : _mapLayerCatalog.SurfaceNamesByMap.TryGetValue(map.Id, out var configuredSurfaceName)
                ? configuredSurfaceName
                : "地面";

        var markerProjection = GetActiveMarkerProjection();
        var sourceMarkers = (map.Markers ?? [])
            .Concat(GetSelectedTaskMarkers())
            .Concat(_viewModel.PlayerMarker is { } player ? [player] : [])
            .Concat(GetLanPeerMarkers())
            .Where(marker => marker.Type is "extract" or "transit" or "task" or "player" or "player-stale" or "player-preview" or "peer")
            .Where(IsMarkerVisible)
            .ToArray();
        var markers = new List<FeatureMobileMapMarker>(sourceMarkers.Length);
        for (var index = 0; index < sourceMarkers.Length; index++)
        {
            var source = sourceMarkers[index];
            var projected = ProjectMarker(source, markerProjection);
            if (!double.IsFinite(projected.X) || !double.IsFinite(projected.Y) ||
                projected.X is < 0 or > 1 || projected.Y is < 0 or > 1)
                continue;

            markers.Add(new FeatureMobileMapMarker(
                $"{NormalizeMobileMapKey(source.Type)}-{index}",
                source.Type,
                source.Label,
                projected.X,
                projected.Y,
                source.HeadingDegrees,
                source.ColorHex));
        }

        var snapshot = new FeatureMobileMapSnapshot(
            map.Id,
            map.Name,
            satelliteActive ? "satellite-map" : "2d",
            _activeMapLayer?.Id,
            _activeMapLayer?.Name ?? surfaceName,
            CreateMobileMapAsset($"{map.Id}-base", basePath),
            CreateMobileMapAsset($"{map.Id}-{_activeMapLayer?.Id ?? "surface"}-layer", _activeMapLayer?.ImageFilePath),
            _activeMapLayer is not null,
            _isListening,
            _viewModel.PlayerMarker is not null,
            _viewModel.IsCoordinateStale,
            _viewModel.LiveCoordinate?.CapturedAt,
            markers);
        if (!satelliteActive) return snapshot;

        return snapshot with
        {
            CompactBaseMap = CreateMobileMapAsset($"{map.Id}-base-compact", GetMobileWebVariantPath(basePath, "2048")),
            SharpBaseMap = CreateMobileMapAsset($"{map.Id}-base-sharp", GetMobileWebVariantPath(basePath, "3072")),
            CompactLayerMap = CreateMobileMapAsset($"{map.Id}-{_activeMapLayer?.Id ?? "surface"}-layer-compact", GetMobileWebVariantPath(_activeMapLayer?.ImageFilePath, "2048")),
            SharpLayerMap = CreateMobileMapAsset($"{map.Id}-{_activeMapLayer?.Id ?? "surface"}-layer-sharp", GetMobileWebVariantPath(_activeMapLayer?.ImageFilePath, "3072"))
        };
    }

    private static string? GetMobileWebVariantPath(string? originalPath, string tier)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return null;
        var webRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "maps", "web"));
        var sourcePath = System.IO.Path.GetFullPath(originalPath);
        var relativePath = System.IO.Path.GetRelativePath(webRoot, sourcePath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relativePath)) return null;
        return System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "maps",
            "mobile-web",
            tier,
            System.IO.Path.ChangeExtension(relativePath, ".webp"));
    }

    private static FeatureMobileMapAsset? CreateMobileMapAsset(string key, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var fullPath = System.IO.Path.GetFullPath(path);
        var applicationRoot = System.IO.Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(applicationRoot, StringComparison.OrdinalIgnoreCase)) return null;

        var contentType = System.IO.Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => null
        };
        return contentType is null
            ? null
            : new FeatureMobileMapAsset(NormalizeMobileMapKey(key), fullPath, contentType);
    }

    private static string NormalizeMobileMapKey(string value)
    {
        var normalized = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(96)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "map" : normalized;
    }

    void IFeatureHost.RefreshMapMarkers()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateMarkers));
            return;
        }

        UpdateMarkers();
    }

    private static FeatureTaskTrackerSnapshot ToFeatureTaskTrackerSnapshot(TaskTrackerLoadResult load) => new(
        load.Items.Select(item => new FeatureTrackerItem(
            item.Id,
            item.Name,
            item.ShortName,
            item.IconLink,
            item.Required,
            item.CompletedRequired,
            item.CompletedTaskRequired,
            item.CompletedHideoutRequired,
            item.FoundInRaidRequired,
            item.TaskRequired,
            item.HideoutRequired,
            item.Have,
            item.FleaPrice,
            item.Avg24hPrice,
            item.Sources.Select(source => new FeatureTrackerRequirementSource(
                source.Key,
                source.Type,
                source.Name,
                source.Detail,
                source.Count,
                source.FoundInRaid,
                source.Trader,
                source.Completed,
                source.Choice)).ToArray(),
            item.Choice,
            item.Choices)).ToArray(),
        load.UpdatedAt,
        load.ErrorMessage,
        load.IsStale,
        load.TotalRequired,
        load.TotalRemaining,
        load.TotalEstimatedCost,
        load.StateVersion);

    async Task<ImageSource?> IFeatureHost.LoadItemIconAsync(
        string itemId,
        string? iconLink,
        string? fallbackIconLink,
        bool preferFullImage) =>
        await ItemIconService.GetAsync(itemId, iconLink, fallbackIconLink, preferFullImage);

    async Task<ImageSource?> IFeatureHost.LoadImageAsync(string imageId, string imageUrl, bool fullSize) =>
        fullSize
            ? await SeasonDocumentImageService.GetFullAsync(imageId, imageUrl)
            : await SeasonDocumentImageService.GetPreviewAsync(imageId, imageUrl);

    private void InitializeFeaturePageRegistry()
    {
        _featurePages =
        [
            new(FeatureRoutes.Market, WorkspacePage.Market, MarketPage, MarketRailButton, MarketModuleHost, true),
            new(FeatureRoutes.InGamePrice, WorkspacePage.InGamePrice, InGamePricePage, InGamePriceRailButton, InGamePriceModuleHost, true),
            new(FeatureRoutes.TaskItems, WorkspacePage.TaskItems, TaskItemsPage, TaskItemsRailButton, TaskItemsModuleHost, true),
            new(FeatureRoutes.Memo, WorkspacePage.Memo, MemoPage, MemoRailButton, MemoModuleHost, true),
            new(FeatureRoutes.TaskTracking, WorkspacePage.TaskTracking, TaskTrackingPage, TaskTrackingRailButton, TaskTrackingModuleHost),
            new(FeatureRoutes.ScreenFilter, WorkspacePage.ScreenFilter, ScreenFilterPage, ScreenFilterRailButton, ScreenFilterModuleHost),
            new(FeatureRoutes.TeamSync, WorkspacePage.TeamSync, TeamSyncPage, TeamSyncRailButton, TeamSyncModuleHost),
            new(FeatureRoutes.MobileMap, WorkspacePage.MobileMap, MobileMapPage, MobileMapRailButton, MobileMapModuleHost),
            new(FeatureRoutes.Utilities, WorkspacePage.Utilities, UtilitiesPage, UtilitiesRailButton, UtilitiesModuleHost)
        ];
    }

    private FeaturePageRegistration? FindFeaturePage(string route) =>
        _featurePages.FirstOrDefault(item => string.Equals(item.Route, route, StringComparison.Ordinal));

    private FeaturePageRegistration? FindFeaturePage(WorkspacePage page) =>
        _featurePages.FirstOrDefault(item => item.Page == page);

    private void InitializeFeatureModules(FeatureModuleCatalog? discoveredModules = null)
    {
        var modulesDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "Modules");
        _featureModules = discoveredModules ?? FeatureModuleCatalog.Discover(modulesDirectory);
        foreach (var issue in _featureModules.Issues)
            RuntimeLogService.Warning("组件", "可选组件加载失败", $"清单: {issue.ManifestPath}\n原因: {issue.Message}");

        foreach (var module in _featureModules.Modules.OrderBy(item => item.Instance.Descriptor.Order))
        {
            var descriptor = module.Instance.Descriptor;
            RuntimeLogService.Info("组件", "已加载可选组件", $"{descriptor.DisplayName} ({descriptor.Id})\n{module.AssemblyPath}");
        }

        foreach (var registration in _featurePages)
        {
            ResetFeaturePage(registration);
            if (!_featureModules.TryGet(registration.Route, out var module)) continue;
            try
            {
                var view = CreateFeatureView(module, registration.Route);
                AttachFeatureView(registration.Route, view);
                registration.Host.Content = view;
                registration.RailButton.Visibility = Visibility.Visible;
            }
            catch (Exception exception)
            {
                ResetFeaturePage(registration);
                RuntimeLogService.Error("组件", $"{module.Instance.Descriptor.DisplayName}组件界面创建失败", exception);
            }
        }
    }

    private void AttachFeatureView(string route, FrameworkElement view)
    {
        switch (route)
        {
            case FeatureRoutes.TaskTracking:
                _taskTrackingFeature = view as ITaskTrackingFeature
                    ?? throw new InvalidOperationException("任务追踪组件未实现交互接口。");
                _taskTrackingFeature.AutomaticTaskRecognitionChanged += TaskTrackingPage_AutomaticTaskRecognitionChanged;
                _taskTrackingFeature.AutoCompletePrerequisitesChanged += TaskTrackingPage_AutoCompletePrerequisitesChanged;
                _taskTrackingFeature.TaskRecognitionRequested += TaskTrackingPage_TaskRecognitionRequested;
                _taskTrackingFeature.TaskModeChanged += TaskTrackingPage_TaskModeChanged;
                _taskStatusMonitor = new TaskStatusLogMonitorService(_taskTrackingFeature.TrackingTaskIdsByGameId);
                break;
            case FeatureRoutes.TeamSync:
                _teamSyncFeature = view as ITeamSyncFeature
                    ?? throw new InvalidOperationException("队友共享组件未实现交互接口。");
                break;
            case FeatureRoutes.MobileMap:
                _mobileMapFeature = view as IMobileMapFeature
                    ?? throw new InvalidOperationException("手机地图组件未实现共享服务接口。");
                break;
            case FeatureRoutes.ScreenFilter:
            case FeatureRoutes.InGamePrice:
                if (view is not IFeatureViewLifecycle)
                    throw new InvalidOperationException("组件未实现界面生命周期接口。");
                break;
        }
    }

    private void ResetFeaturePage(FeaturePageRegistration registration)
    {
        registration.RailButton.Visibility = Visibility.Collapsed;
        registration.Host.Content = null;
        if (registration.Route == FeatureRoutes.TaskTracking)
        {
            _taskTrackingFeature = null;
            _taskStatusMonitor = null;
        }
        else if (registration.Route == FeatureRoutes.TeamSync)
        {
            _teamSyncFeature = null;
        }
        else if (registration.Route == FeatureRoutes.MobileMap)
        {
            _mobileMapFeature = null;
        }
    }
    private FrameworkElement CreateFeatureView(LoadedFeatureModule module, string route)
    {
        var view = module.Instance.CreateView(this, route);
        if (view is IAsyncDisposable lifecycle)
            _featureViewLifecycles.Add((module.Instance.Descriptor.DisplayName, lifecycle));
        return view;
    }

    private async Task DisposeFeatureViewsAsync()
    {
        for (var index = _featureViewLifecycles.Count - 1; index >= 0; index--)
        {
            var feature = _featureViewLifecycles[index];
            try
            {
                await feature.Lifecycle.DisposeAsync();
            }
            catch (Exception exception)
            {
                RuntimeLogService.Warning(
                    "组件",
                    $"关闭时释放可选组件“{feature.DisplayName}”失败",
                    exception.GetBaseException().Message);
            }
        }
        _featureViewLifecycles.Clear();
    }

    internal void VerifyInstalledModuleViews()
    {
        foreach (var module in _featureModules.Modules)
        {
            var route = module.Instance.Descriptor.Id;
            var registration = FindFeaturePage(route)
                ?? throw new InvalidDataException($"未识别的组件路由：{route}");
            var view = registration.Host.Content;
            if (view is null)
                throw new InvalidDataException($"组件界面创建失败：{module.Instance.Descriptor.DisplayName} ({route})");
        }
    }

    private void ShowWorkspacePage(WorkspacePage page)
    {
        var pageChanged = _activePage != page;
        if (pageChanged && !_isNavigatingHistory)
        {
            _backHistory.Push(_activePage);
            _forwardHistory.Clear();
        }
        if (pageChanged) RuntimeLogService.Trace("界面", "切换工作区页面", $"原页面: {_activePage}\n新页面: {page}");
        _activePage = page;
        var activeFeaturePage = FindFeaturePage(page);
        if (activeFeaturePage?.UsesMarketData == true)
            _marketMemoryReleaseRequested = false;
        SetWorkspacePageVisibility(MapPage, page == WorkspacePage.Map, pageChanged);
        foreach (var featurePage in _featurePages)
            SetWorkspacePageVisibility(featurePage.PageElement, page == featurePage.Page, pageChanged);
        SetWorkspacePageVisibility(SettingsPage, page == WorkspacePage.Settings, pageChanged);
        SetRailButton(MapRailButton, page == WorkspacePage.Map);
        foreach (var featurePage in _featurePages)
            SetRailButton(featurePage.RailButton, page == featurePage.Page);
        SetRailButton(SettingsRailButton, page == WorkspacePage.Settings);
        ApplyResponsiveLayout(force: true);

        if (activeFeaturePage?.Host.Content is IFeatureViewLifecycle lifecycle)
            lifecycle.OnActivated();
        if (activeFeaturePage?.Route == FeatureRoutes.InGamePrice)
        {
            _ = InitializeMarketOnStartupAsync();
        }
        if (activeFeaturePage?.Route == FeatureRoutes.TaskTracking)
            _taskTrackingFeature?.EnsureInitialized();
        if (page == WorkspacePage.Settings)
        {
            UpdateSettingsPage();
            _ = RefreshSettingsSnapshotAsync();
        }
        ReleaseMarketMemoryIfIdle();
    }

    private bool NavigateBack()
    {
        var moduleNavigation = FindFeaturePage(_activePage)?.Host.Content as IFeatureNavigationHandler;
        if (moduleNavigation is not null &&
            moduleNavigation.NavigateBack())
            return true;
        return NavigateHistory(_backHistory, _forwardHistory);
    }

    private bool NavigateForward()
    {
        var moduleNavigation = FindFeaturePage(_activePage)?.Host.Content as IFeatureNavigationHandler;
        if (moduleNavigation is not null &&
            moduleNavigation.NavigateForward())
            return true;
        return NavigateHistory(_forwardHistory, _backHistory);
    }

    private bool NavigateHistory(Stack<WorkspacePage> source, Stack<WorkspacePage> destination)
    {
        if (!source.TryPop(out var target)) return false;
        destination.Push(_activePage);
        _isNavigatingHistory = true;
        try { ShowWorkspacePage(target); }
        finally { _isNavigatingHistory = false; }
        return true;
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        if (FindFeaturePage(_activePage)?.Host.Content is IFeatureMouseNavigationGuard guard &&
            guard.ShouldSuppressNavigation(e.ChangedButton, e.OriginalSource as DependencyObject))
            return;

        e.Handled = e.ChangedButton == MouseButton.XButton1 ? NavigateBack() : NavigateForward();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void SetRailButton(Button button, bool isActive)
    {
        button.Background = isActive ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent;
        button.BorderBrush = isActive ? (Brush)FindResource("LineBrightBrush") : Brushes.Transparent;
        button.Foreground = isActive ? (Brush)FindResource("AmberBrush") : (Brush)FindResource("TextDimBrush");
    }

    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    private void SetWorkspacePageVisibility(FrameworkElement workspacePage, bool isActive, bool animate)
    {
        if (!isActive)
        {
            workspacePage.BeginAnimation(OpacityProperty, null);
            if (workspacePage.RenderTransform is TranslateTransform outgoingTransform)
                outgoingTransform.BeginAnimation(TranslateTransform.XProperty, null);
            workspacePage.Opacity = 1;
            workspacePage.Visibility = Visibility.Collapsed;
            return;
        }

        workspacePage.Visibility = Visibility.Visible;
        if (!animate || !MotionEnabled)
        {
            workspacePage.BeginAnimation(OpacityProperty, null);
            workspacePage.Opacity = 1;
            if (workspacePage.RenderTransform is TranslateTransform staticTransform)
            {
                staticTransform.BeginAnimation(TranslateTransform.XProperty, null);
                staticTransform.X = 0;
            }
            return;
        }

        var transform = EnsureTranslateTransform(workspacePage);
        var duration = new Duration(TimeSpan.FromMilliseconds(185));
        workspacePage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private static TranslateTransform EnsureTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Point(.5, .5);
        return transform;
    }

    private void ReleaseMarketMemoryIfIdle()
    {
        if (!_marketMemoryReleaseRequested ||
            _marketAutomaticRefreshRunning ||
            _marketStartupInitializationRunning ||
            FindFeaturePage(_activePage)?.UsesMarketData == true)
            return;

        _marketMemoryReleaseRequested = false;
        _marketStartupInitializationStarted = false;
        _marketStartupRefreshStarted = false;
        _marketCatalog = new MarketCatalog([], [], null, "", "行情缓存未加载");
        MarketPriceService.ReleaseMemory();
        ItemIconService.ReleaseMemory();
        SeasonDocumentImageService.ReleaseMemory();
        RuntimeLogService.Info("内存", "已释放可重建缓存", "行情目录、列表结果和图片内存缓存已清理；再次打开功能时会从磁盘缓存重建。");
    }



    private async void StartMarketAutomaticRefresh(bool forceRefresh = false)
    {
        if (_marketAutomaticRefreshRunning) return;
        if (forceRefresh)
        {
            if (_marketStartupRefreshStarted) return;
            _marketStartupRefreshStarted = true;
        }
        else if (_marketCatalog.IsComplete && !_marketCatalog.IsStale && MarketPriceService.IsJsonApiCatalog(_marketCatalog))
        {
            return;
        }

        _marketAutomaticRefreshRunning = true;
        var fallbackCatalog = _marketCatalog;
        var refreshWatch = Stopwatch.StartNew();
        RuntimeLogService.Info(
            "市场",
            forceRefresh ? "开始启动时行情刷新" : "开始后台行情刷新",
            $"缓存可用: {fallbackCatalog.IsAvailable}\n缓存过期: {fallbackCatalog.IsStale}\n" +
            $"缓存更新时间: {fallbackCatalog.UpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}\n" +
            $"缓存数量: PVP {fallbackCatalog.PvpItems.Count:N0} · PVE {fallbackCatalog.PveItems.Count:N0} · 赛季服 {(fallbackCatalog.PvpSeasonItems?.Count ?? 0):N0}");
        try
        {
            var catalog = forceRefresh
                ? await MarketPriceService.RefreshAsync(_lifetimeCts.Token)
                : await MarketPriceService.EnsureFreshAsync(_lifetimeCts.Token);
            ApplyFeatureMarketCatalog(catalog);
            refreshWatch.Stop();
            var detail = $"耗时: {refreshWatch.Elapsed.TotalMilliseconds:N0} ms\n来源: {_marketCatalog.Source}\n" +
                         $"更新时间: {_marketCatalog.UpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}\n" +
                         $"物品数量: PVP {_marketCatalog.PvpItems.Count:N0} · PVE {_marketCatalog.PveItems.Count:N0} · 赛季服 {(_marketCatalog.PvpSeasonItems?.Count ?? 0):N0}\n" +
                         $"服务消息: {_marketCatalog.ErrorMessage ?? "无"}";
            if (string.IsNullOrWhiteSpace(_marketCatalog.ErrorMessage))
                RuntimeLogService.Info("市场", "后台行情刷新完成", detail);
            else
                RuntimeLogService.Warning("市场", "后台行情刷新返回警告", detail);
        }
        catch (OperationCanceledException) when (_isWindowClosing) { return; }
        catch (Exception exception)
        {
            refreshWatch.Stop();
            var diskFallback = MarketPriceService.Load();
            ApplyFeatureMarketCatalog(fallbackCatalog.IsAvailable ? fallbackCatalog : diskFallback);
            RuntimeLogService.Error(
                "市场",
                "后台行情刷新失败",
                exception,
                $"耗时: {refreshWatch.Elapsed.TotalMilliseconds:N0} ms\n回退缓存: {(_marketCatalog.IsAvailable ? "可用" : "不可用")}\n" +
                $"回退数量: PVP {_marketCatalog.PvpItems.Count:N0} · PVE {_marketCatalog.PveItems.Count:N0} · 赛季服 {(_marketCatalog.PvpSeasonItems?.Count ?? 0):N0}");
        }
        finally
        {
            _marketAutomaticRefreshRunning = false;
            ReleaseMarketMemoryIfIdle();
        }
    }

    private async Task InitializeMarketOnStartupAsync()
    {
        if (_marketStartupInitializationStarted) return;
        _marketStartupInitializationStarted = true;
        _marketStartupInitializationRunning = true;
        var watch = Stopwatch.StartNew();
        try
        {
            ApplyFeatureMarketCatalog(await Task.Run(MarketPriceService.Load));
            watch.Stop();
            RuntimeLogService.Info(
                "市场",
                "行情缓存后台加载完成",
                $"耗时: {watch.Elapsed.TotalMilliseconds:N0} ms\n缓存可用: {_marketCatalog.IsAvailable}\n" +
                $"缓存数量: PVP {_marketCatalog.PvpItems.Count:N0} · PVE {_marketCatalog.PveItems.Count:N0} · 赛季服 {(_marketCatalog.PvpSeasonItems?.Count ?? 0):N0}\n" +
                $"更新时间: {_marketCatalog.UpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}");
        }
        catch (Exception exception)
        {
            watch.Stop();
            ApplyFeatureMarketCatalog(new MarketCatalog([], [], null, "", "读取本地行情缓存失败。"));
            RuntimeLogService.Error("市场", "后台读取行情缓存失败", exception, $"耗时: {watch.Elapsed.TotalMilliseconds:N0} ms");
        }
        finally
        {
            _marketStartupInitializationRunning = false;
        }

        if (_isWindowClosing) return;
        if (_marketMemoryReleaseRequested)
        {
            ReleaseMarketMemoryIfIdle();
            return;
        }
        StartMarketAutomaticRefresh(forceRefresh: true);
    }

    private void UpdateSettingsPage()
    {
        if (SettingsPage is null) return;
        _isUpdatingCloseBehaviorUi = true;
        try
        {
            CloseBehaviorComboBox.SelectedIndex = !_desktopPreferences.SuppressClosePrompt
                ? 0
                : _desktopPreferences.MinimizeWhenClosing ? 1 : 2;
            CloseBehaviorStatusText.Text = !_desktopPreferences.SuppressClosePrompt
                ? "当前：每次点击关闭都会询问。"
                : _desktopPreferences.MinimizeWhenClosing
                    ? "当前：关闭窗口时最小化；悬浮地图与窗口化 OCR 调试框会继续显示。"
                    : "当前：关闭窗口时直接退出工具。";
        }
        finally
        {
            _isUpdatingCloseBehaviorUi = false;
        }
        SettingsScreenshotPathText.Text = _desktopPreferences.ScreenshotDirectory ?? "尚未选择";
        SettingsScreenshotStateText.Text = string.IsNullOrWhiteSpace(_desktopPreferences.ScreenshotDirectory)
            ? "请选择 Escape from Tarkov 截图目录"
            : Directory.Exists(_desktopPreferences.ScreenshotDirectory) ? "目录可访问" : "目录当前不可访问";
        SettingsGameLogPathText.Text = _desktopPreferences.GameLogDirectory ?? "尚未选择";
        SettingsGameLogStateText.Text = string.IsNullOrWhiteSpace(_desktopPreferences.GameLogDirectory)
            ? "请选择 Escape from Tarkov 的 Logs 目录"
            : Directory.Exists(_desktopPreferences.GameLogDirectory) ? "目录可访问" : "目录当前不可访问";
        FirstRunSetupBorder.Visibility = _requiresInitialPathSetup ? Visibility.Visible : Visibility.Collapsed;
        RefreshRuntimeLogView(force: true);
    }

    private void CloseBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCloseBehaviorUi || !IsLoaded || CloseBehaviorComboBox.SelectedIndex < 0) return;

        var selectedIndex = CloseBehaviorComboBox.SelectedIndex;
        var suppressPrompt = selectedIndex != 0;
        var minimizeWhenClosing = selectedIndex == 1;
        if (_desktopPreferences.SuppressClosePrompt == suppressPrompt &&
            _desktopPreferences.MinimizeWhenClosing == minimizeWhenClosing) return;

        if (!DesktopPreferencesService.SaveCloseBehavior(suppressPrompt, minimizeWhenClosing))
        {
            RuntimeLogService.Warning("配置", "保存关闭行为失败", "已恢复到当前会话前的关闭偏好。");
            UpdateSettingsPage();
            ShowToast("关闭行为保存失败，请检查本机配置目录权限");
            return;
        }

        _desktopPreferences = _desktopPreferences with
        {
            SuppressClosePrompt = suppressPrompt,
            MinimizeWhenClosing = minimizeWhenClosing
        };
        RuntimeLogService.Info(
            "配置",
            "已更新关闭行为",
            suppressPrompt
                ? minimizeWhenClosing ? "关闭窗口：最小化" : "关闭窗口：退出工具"
                : "关闭窗口：每次询问");
        UpdateSettingsPage();
        ShowToast(suppressPrompt
            ? minimizeWhenClosing ? "关闭行为已设为最小化" : "关闭行为已设为退出工具"
            : "关闭行为已设为每次询问");
    }

    private void RuntimeLogService_Changed()
    {
        _runtimeLogViewDirty = true;
        if (_isWindowClosing || _activePage != WorkspacePage.Settings) return;
        if (!Dispatcher.CheckAccess())
        {
            if (Interlocked.Exchange(ref _runtimeLogRefreshDispatchQueued, 1) == 0)
                Dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _runtimeLogRefreshDispatchQueued, 0);
                    RuntimeLogService_Changed();
                }, DispatcherPriority.Background);
            return;
        }

        if (!_runtimeLogRefreshTimer.IsEnabled) _runtimeLogRefreshTimer.Start();
    }

    private void InitializeRuntimeLogControls()
    {
        _updatingRuntimeLogFilters = true;
        try
        {
            RuntimeLogCategoryFilterComboBox.ItemsSource = new[] { "全部模块" };
            RuntimeLogCategoryFilterComboBox.SelectedIndex = 0;
        }
        finally
        {
            _updatingRuntimeLogFilters = false;
        }
    }

    private void RuntimeLogFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingRuntimeLogFilters) return;
        _runtimeLogViewDirty = true;
        if (_activePage == WorkspacePage.Settings) RefreshRuntimeLogView(force: true);
    }

    private void ToggleRuntimeLogAutoScroll_Click(object sender, RoutedEventArgs e)
    {
        _runtimeLogAutoScroll = !_runtimeLogAutoScroll;
        RuntimeLogAutoScrollButton.Content = _runtimeLogAutoScroll ? "自动滚动：开" : "自动滚动：关";
        RuntimeLogAutoScrollButton.Foreground = _runtimeLogAutoScroll
            ? (Brush)FindResource("GreenBrush")
            : (Brush)FindResource("TextDimBrush");
        if (_runtimeLogAutoScroll)
        {
            RuntimeLogTextBox.CaretIndex = RuntimeLogTextBox.Text.Length;
            RuntimeLogTextBox.ScrollToEnd();
        }
    }

    private void RefreshRuntimeLogView(bool force = false)
    {
        if (RuntimeLogTextBox is null || (!force && !_runtimeLogViewDirty)) return;

        var lastVisibleLine = RuntimeLogTextBox.GetLastVisibleLineIndex();
        var followTail = _runtimeLogAutoScroll &&
                         (string.IsNullOrEmpty(RuntimeLogTextBox.Text) ||
                          lastVisibleLine < 0 ||
                          lastVisibleLine >= RuntimeLogTextBox.LineCount - 2);
        var entries = RuntimeLogService.GetSessionEntries();
        RefreshRuntimeLogCategoryOptions(entries);
        var level = RuntimeLogLevelFilterComboBox.SelectedIndex switch
        {
            1 => RuntimeLogLevel.Trace,
            2 => RuntimeLogLevel.Info,
            3 => RuntimeLogLevel.Warning,
            4 => RuntimeLogLevel.Error,
            _ => (RuntimeLogLevel?)null
        };
        var category = RuntimeLogCategoryFilterComboBox.SelectedIndex > 0
            ? RuntimeLogCategoryFilterComboBox.SelectedItem as string
            : null;
        var filteredEntries = RuntimeLogService.FilterEntries(entries, level, category, RuntimeLogSearchBox.Text);
        _runtimeLogFilteredText = RuntimeLogService.FormatEntries(filteredEntries);
        _runtimeLogFilteredEntryCount = filteredEntries.Length;
        if (!string.Equals(RuntimeLogTextBox.Text, _runtimeLogFilteredText, StringComparison.Ordinal))
            RuntimeLogTextBox.Text = _runtimeLogFilteredText;

        var health = RuntimeLogService.GetHealth();
        var filterActive = level is not null || category is not null || !string.IsNullOrWhiteSpace(RuntimeLogSearchBox.Text);
        RuntimeLogSummaryText.Text = filterActive
            ? $"显示 {_runtimeLogFilteredEntryCount:N0} / {health.SessionEntryCount:N0} 条"
            : $"当前会话 {health.SessionEntryCount:N0} 条 · {(health.SessionEntryCount > 0 ? "实时记录中" : "等待新事件")}";
        RuntimeLogSummaryText.Foreground = health.LastWriteError is null
            ? (Brush)FindResource("GreenBrush")
            : (Brush)FindResource("AmberBrush");
        RuntimeLogRetentionText.Text = health.LastWriteError is null
            ? health.PendingFileEntryCount > 0
                ? $"等待写盘 {health.PendingFileEntryCount:N0} 条 · 保留 14 天"
                : health.LastSuccessfulWriteAt is { } writtenAt
                    ? $"已写盘 {writtenAt.LocalDateTime:HH:mm:ss} · 保留 14 天"
                    : "完整日志按天保存 · 保留 14 天"
            : $"磁盘写入异常 · 未写盘 {health.LostFileEntryCount:N0} 条";
        RuntimeLogRetentionText.Foreground = health.LastWriteError is null
            ? (Brush)FindResource("TextFaintBrush")
            : (Brush)FindResource("AmberBrush");
        RuntimeLogRetentionText.ToolTip = health.LastWriteError;
        RuntimeLogPathText.Text = $"日志文件：{RuntimeLogService.CurrentLogFilePath}";
        RuntimeLogPathText.ToolTip = RuntimeLogService.CurrentLogFilePath;
        _runtimeLogViewDirty = false;

        if (followTail)
        {
            RuntimeLogTextBox.CaretIndex = RuntimeLogTextBox.Text.Length;
            RuntimeLogTextBox.ScrollToEnd();
        }
    }

    private void RefreshRuntimeLogCategoryOptions(IReadOnlyCollection<RuntimeLogEntry> entries)
    {
        var selected = RuntimeLogCategoryFilterComboBox.SelectedItem as string ?? "全部模块";
        var options = entries.Select(entry => entry.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
            .Prepend("全部模块")
            .ToArray();
        var existing = RuntimeLogCategoryFilterComboBox.Items.Cast<object>()
            .Select(item => item?.ToString() ?? string.Empty)
            .ToArray();
        if (existing.SequenceEqual(options, StringComparer.Ordinal)) return;

        _updatingRuntimeLogFilters = true;
        try
        {
            RuntimeLogCategoryFilterComboBox.ItemsSource = options;
            RuntimeLogCategoryFilterComboBox.SelectedItem = options.FirstOrDefault(
                option => string.Equals(option, selected, StringComparison.OrdinalIgnoreCase)) ?? "全部模块";
        }
        finally
        {
            _updatingRuntimeLogFilters = false;
        }
    }

    private void CopyRuntimeLog_Click(object sender, RoutedEventArgs e)
    {
        RefreshRuntimeLogView(force: true);
        if (string.IsNullOrWhiteSpace(_runtimeLogFilteredText))
        {
            ShowToast("当前筛选结果为空");
            return;
        }

        try
        {
            Clipboard.SetText(_runtimeLogFilteredText);
            ShowToast($"已复制 {_runtimeLogFilteredEntryCount:N0} 条运行日志");
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("运行日志", "复制运行日志失败", exception);
            ShowToast("复制运行日志失败");
        }
    }

    private void ExportRuntimeLog_Click(object sender, RoutedEventArgs e)
    {
        RefreshRuntimeLogView(force: true);
        if (string.IsNullOrWhiteSpace(_runtimeLogFilteredText))
        {
            ShowToast("当前筛选结果为空");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出运行日志",
            Filter = "文本日志 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"TarkovMapLocator-运行日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _runtimeLogFilteredText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            RuntimeLogService.Info("运行日志", "已导出筛选结果", $"条目: {_runtimeLogFilteredEntryCount:N0}\n文件: {dialog.FileName}");
            ShowToast($"已导出 {_runtimeLogFilteredEntryCount:N0} 条运行日志");
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("运行日志", "导出运行日志失败", exception, dialog.FileName);
            ShowToast("导出运行日志失败");
        }
    }

    private void ClearRuntimeLog_Click(object sender, RoutedEventArgs e)
    {
        RuntimeLogService.ClearSession();
        RefreshRuntimeLogView(force: true);
        ShowToast("已清空当前会话的日志显示；磁盘日志仍保留");
    }

    private void OpenRuntimeLogDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RuntimeLogService.LogDirectory);
            Process.Start(new ProcessStartInfo(RuntimeLogService.LogDirectory) { UseShellExecute = true });
            RuntimeLogService.Info("运行日志", "已打开日志目录", RuntimeLogService.LogDirectory);
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("运行日志", "打开日志目录失败", exception, RuntimeLogService.LogDirectory);
            ShowToast("无法打开运行日志目录");
        }
    }

    private async void ChooseScreenshotPath_Click(object sender, RoutedEventArgs e) => await ChooseDirectoryAsync(
        "选择 Escape from Tarkov 截图目录",
        _desktopPreferences.ScreenshotDirectory,
        path => _desktopPreferences = _desktopPreferences with { ScreenshotDirectory = path });

    private async void ChooseGameLogPath_Click(object sender, RoutedEventArgs e) => await ChooseDirectoryAsync(
        "选择 Escape from Tarkov 的 Logs 目录",
        _desktopPreferences.GameLogDirectory,
        path => _desktopPreferences = _desktopPreferences with { GameLogDirectory = path });

    private async Task ChooseDirectoryAsync(string title, string? initialDirectory, Action<string> apply)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        apply(dialog.FolderName);
        var saved = DesktopPreferencesService.SavePaths(_desktopPreferences.ScreenshotDirectory, _desktopPreferences.GameLogDirectory);
        _requiresInitialPathSetup = string.IsNullOrWhiteSpace(_desktopPreferences.ScreenshotDirectory) || string.IsNullOrWhiteSpace(_desktopPreferences.GameLogDirectory);
        RuntimeLogService.Info(
            "配置",
            "用户更新本机读取路径",
            $"选择项: {title}\n原路径: {initialDirectory ?? "未配置"}\n新路径: {dialog.FolderName}\n" +
            $"截图目录: {_desktopPreferences.ScreenshotDirectory ?? "未配置"}\n游戏日志目录: {_desktopPreferences.GameLogDirectory ?? "未配置"}\n" +
            $"首次配置完成: {!_requiresInitialPathSetup}");
        if (_isListening) await RefreshLocalRaidDataAsync();
        else
        {
            UpdateListeningControls();
            await RefreshSettingsSnapshotAsync();
        }
        UpdateSettingsPage();
        ShowToast(!saved
            ? "路径已应用，但配置写入失败；重启后需要重新选择"
            : _requiresInitialPathSetup ? "还需要选择另一项路径" : "本机路径已配置，监听已更新");
    }

    private Border CreateToolRow()
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(21, 28, 25)),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 7),
            RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = new TranslateTransform()
        };
        row.MouseEnter += (_, _) => AnimateToolRow(row, true);
        row.MouseLeave += (_, _) => AnimateToolRow(row, false);
        return row;
    }

    private void AnimateToolRow(Border row, bool isHovering)
    {
        if (!MotionEnabled || row.RenderTransform is not TranslateTransform transform) return;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(isHovering ? 2 : 0, new Duration(TimeSpan.FromMilliseconds(isHovering ? 135 : 175)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetToolButton(Button button, bool selected)
    {
        button.Background = selected ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent;
        button.BorderBrush = selected ? (Brush)FindResource("LineBrightBrush") : (Brush)FindResource("LineBrush");
        button.Foreground = selected ? (Brush)FindResource("AmberBrush") : (Brush)FindResource("TextDimBrush");
    }

    private static string FormatPrice(long? value) => value is { } price and > 0 ? $"{price:N0} ₽" : "—";

    private void MiniMapToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_miniMapWindow is { IsVisible: true })
        {
            _miniMapWindow.Close();
            return;
        }

        var miniMap = new MiniMapWindow();
        var workArea = SystemParameters.WorkArea;
        miniMap.Left = Math.Clamp(Left + Width - miniMap.Width, workArea.Left, workArea.Right - miniMap.Width);
        miniMap.Top = Math.Clamp(Top + 36, workArea.Top, workArea.Bottom - miniMap.Height);
        miniMap.Closed += (_, _) =>
        {
            if (ReferenceEquals(_miniMapWindow, miniMap)) _miniMapWindow = null;
        };
        _miniMapWindow = miniMap;
        miniMap.Show();
        UpdateMarkers();
        ShowToast("已打开悬浮地图");
    }

    private async Task RefreshLocalRaidDataAsync()
    {
        var snapshot = await ReadLatestRaidSnapshotAsync();
        if (snapshot is null) return;

        ApplyLocalRaidSnapshot(snapshot);
    }

    private async Task RefreshSettingsSnapshotAsync()
    {
        var snapshot = await ReadLatestRaidSnapshotAsync();
        if (snapshot is null || _activePage != WorkspacePage.Settings) return;

        UpdateSettingsPage();
    }

    private async void TaskTrackingPage_AutomaticTaskRecognitionChanged(object? sender, EventArgs e)
    {
        var enabled = _taskTrackingFeature?.AutomaticTaskRecognitionEnabled == true;
        _desktopPreferences = _desktopPreferences with { AutoRecognizeTaskStatuses = enabled };
        var saved = DesktopPreferencesService.SaveTaskRecognitionState(enabled);
        RuntimeLogService.Info(
            "任务识别",
            enabled ? "已开启自动任务识别" : "已关闭自动任务识别",
            $"配置保存: {(saved ? "成功" : "失败")}");
        ShowToast(enabled ? "自动识别任务已开启" : "自动识别任务已关闭");

        if (enabled && !string.IsNullOrWhiteSpace(_desktopPreferences.GameLogDirectory))
            await SynchronizeTaskStatusesAsync(_desktopPreferences.GameLogDirectory);
    }

    private void TaskTrackingPage_AutoCompletePrerequisitesChanged(object? sender, EventArgs e)
    {
        var enabled = _taskTrackingFeature?.AutoCompletePrerequisitesEnabled == true;
        _desktopPreferences = _desktopPreferences with { AutoCompleteTaskPrerequisites = enabled };
        var saved = DesktopPreferencesService.SaveTaskPrerequisiteCompletionState(enabled);
        var completedCount = enabled ? _taskTrackingFeature?.ApplyPrerequisiteCompletionToExistingStatuses() ?? 0 : 0;
        RuntimeLogService.Info(
            "任务追踪",
            enabled ? "已开启自动补全前置任务" : "已关闭自动补全前置任务",
            $"配置保存: {(saved ? "成功" : "失败")}\n自动补全: {completedCount}");
        ShowToast(enabled
            ? completedCount > 0 ? $"已补全 {completedCount} 个前置任务" : "自动补全前置任务已开启"
            : "自动补全前置任务已关闭");
    }

    private async void TaskTrackingPage_TaskRecognitionRequested(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_desktopPreferences.GameLogDirectory))
        {
            ShowToast("请先配置游戏日志目录");
            return;
        }

        if (_taskTrackingFeature is null) return;
        _taskTrackingFeature.SetTaskRecognitionBusy(true);
        try
        {
            var synchronized = await SynchronizeTaskStatusesAsync(_desktopPreferences.GameLogDirectory);
            ShowToast(synchronized ? "任务状态同步完成" : "任务状态同步失败");
        }
        finally
        {
            _taskTrackingFeature?.SetTaskRecognitionBusy(false);
        }
    }

    private async void TaskTrackingPage_TaskModeChanged(object? sender, EventArgs e)
    {
        if (_taskTrackingFeature?.AutomaticTaskRecognitionEnabled != true ||
            string.IsNullOrWhiteSpace(_desktopPreferences.GameLogDirectory))
            return;
        await PrimeTaskStatusMonitoringAsync(_desktopPreferences.GameLogDirectory);
    }

    private async Task<bool> SynchronizeTaskStatusesAsync(string? gameLogDirectory)
    {
        if (_isWindowClosing) return false;
        try
        {
            var taskMode = _taskTrackingFeature?.SelectedTaskMode ?? "pve";
            var changes = await Task.Run(() => ReadTaskStatusChanges(gameLogDirectory, taskMode, replayHistory: true));
            if (_isWindowClosing) return false;
            ApplyTaskStatusChanges(changes);
            return true;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("任务识别", "手动同步任务状态失败", exception, $"游戏日志目录: {gameLogDirectory ?? "未配置"}");
            return false;
        }
    }

    private async Task PrimeTaskStatusMonitoringAsync(string? gameLogDirectory)
    {
        if (_isWindowClosing) return;
        try
        {
            var taskMode = _taskTrackingFeature?.SelectedTaskMode ?? "pve";
            await Task.Run(() => ReadTaskStatusChanges(gameLogDirectory, taskMode, replayHistory: false));
            RuntimeLogService.Info("任务识别", "已切换任务识别模式", $"模式: {taskMode}\n旧日志不会自动导入该模式。");
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("任务识别", "切换任务识别模式失败", exception, $"游戏日志目录: {gameLogDirectory ?? "未配置"}");
        }
    }

    private IReadOnlyList<DetectedTaskStatusChange> ReadTaskStatusChanges(
        string? gameLogDirectory,
        string taskMode,
        bool replayHistory)
    {
        var monitor = _taskStatusMonitor;
        if (monitor is null) return [];
        lock (monitor)
        {
            monitor.SetMode(taskMode);
            return replayHistory
                ? monitor.ReplayHistory(gameLogDirectory)
                : monitor.ReadLatest(gameLogDirectory, replayHistoryWhenUninitialized: false);
        }
    }

    private int ApplyTaskStatusChanges(IReadOnlyList<DetectedTaskStatusChange> changes)
    {
        if (_taskTrackingFeature is null) return 0;
        var featureChanges = changes.Select(change => new FeatureDetectedTaskStatusChange(
            change.TrackingTaskId,
            change.Status switch
            {
                TaskTrackingStatus.Accepted => FeatureTaskTrackingStatus.Accepted,
                TaskTrackingStatus.Completed => FeatureTaskTrackingStatus.Completed,
                _ => FeatureTaskTrackingStatus.NotStarted
            },
            change.GameTaskId,
            change.ObservedAt,
            change.SourceFileName)).ToArray();
        var appliedCount = _taskTrackingFeature.ApplyDetectedTaskStatuses(featureChanges);
        if (appliedCount <= 0) return appliedCount;

        var accepted = changes.Count(change => change.Status == TaskTrackingStatus.Accepted);
        var completed = changes.Count(change => change.Status == TaskTrackingStatus.Completed);
        var failed = changes.Count(change => change.Status == TaskTrackingStatus.NotStarted);
        RuntimeLogService.Info(
            "任务识别",
            "已从游戏日志同步任务追踪状态",
            $"更新任务: {appliedCount}\n" +
            $"已接取: {accepted}\n已完成: {completed}\n已失败或重置: {failed}\n" +
            $"日志文件: {changes.LastOrDefault()?.SourceFileName ?? "未知"}");
        return appliedCount;
    }

    private async Task<LocalRaidSnapshot?> ReadLatestRaidSnapshotAsync()
    {
        if (_isWindowClosing) return null;
        if (_isRaidSnapshotRefreshRunning)
        {
            if (DateTimeOffset.Now - _lastRaidSkipTraceAt >= TimeSpan.FromSeconds(30))
            {
                _lastRaidSkipTraceAt = DateTimeOffset.Now;
                RuntimeLogService.Trace("监听", "跳过本轮读取", "上一轮日志或截图读取尚未结束，避免并发扫描同一目录。");
            }
            return null;
        }

        _isRaidSnapshotRefreshRunning = true;
        var readWatch = Stopwatch.StartNew();
        try
        {
            var readTaskStatuses = _taskTrackingFeature?.AutomaticTaskRecognitionEnabled == true;
            var taskMode = _taskTrackingFeature?.SelectedTaskMode ?? "pve";
            var readResult = await Task.Run(() =>
            {
                var snapshot = _raidMonitor.ReadLatest();
                var taskStatusChanges = readTaskStatuses
                    ? ReadTaskStatusChanges(snapshot.GameLogDirectory, taskMode, replayHistory: false)
                    : [];
                return (Snapshot: snapshot, TaskStatusChanges: taskStatusChanges);
            });
            if (_isWindowClosing) return null;

            var snapshot = readResult.Snapshot;
            ApplyTaskStatusChanges(readResult.TaskStatusChanges);

            _lastRaidSnapshot = snapshot;
            readWatch.Stop();
            var traceSignature = BuildRaidTraceSignature(snapshot);
            var traceNow = DateTimeOffset.Now;
            if (!string.Equals(traceSignature, _lastRaidReadTraceSignature, StringComparison.Ordinal) ||
                traceNow - _lastRaidReadTraceAt >= TimeSpan.FromMinutes(1))
            {
                _lastRaidReadTraceSignature = traceSignature;
                _lastRaidReadTraceAt = traceNow;
                RuntimeLogService.Trace("监听", "完成一轮本机数据读取", BuildRaidSnapshotDetail(snapshot, readWatch.Elapsed));
            }
            return snapshot;
        }
        catch (Exception exception)
        {
            readWatch.Stop();
            RuntimeLogService.Error("监听", "读取本机日志或截图失败", exception, $"耗时: {readWatch.Elapsed.TotalMilliseconds:N0} ms\n截图目录: {_desktopPreferences.ScreenshotDirectory ?? "未配置"}\n游戏日志目录: {_desktopPreferences.GameLogDirectory ?? "未配置"}");
            return null;
        }
        finally
        {
            _isRaidSnapshotRefreshRunning = false;
        }
    }

    private void ApplyLocalRaidSnapshot(LocalRaidSnapshot snapshot)
    {
        var mapSignature = BuildRaidMapSignature(snapshot);
        var hasNewMapEvidence = IsMapEvidenceAuthoritative(snapshot) &&
                                mapSignature is not null &&
                                !string.Equals(mapSignature, _lastAppliedRaidMapSignature, StringComparison.Ordinal);
        var mapChanged = false;
        if (hasNewMapEvidence)
        {
            _lastAppliedRaidMapSignature = mapSignature;
            var previousMap = _viewModel.SelectedMap;
            mapChanged = _viewModel.SelectMapFromLog(snapshot.MapKey);
            if (mapChanged && !ReferenceEquals(MapList.SelectedItem, _viewModel.SelectedMap))
            {
                _isApplyingAutomaticMapSelection = true;
                try { MapList.SelectedItem = _viewModel.SelectedMap; }
                finally { _isApplyingAutomaticMapSelection = false; }
            }

            RuntimeLogService.Info(
                "地图识别",
                mapChanged ? "检测到新的地图证据并完成切换" : "检测到新的地图证据，当前地图无需切换",
                $"识别键: {snapshot.MapKey}\n" +
                $"识别来源: {snapshot.MapSource ?? "未知"}\n" +
                $"识别详情: {snapshot.MapDetectionDetail ?? "无"}\n" +
                $"地图识别时间: {snapshot.MapObservedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未知"}\n" +
                $"原地图: {previousMap?.Name ?? "无"} ({previousMap?.Id ?? "none"})\n" +
                $"当前地图: {_viewModel.CurrentMapName} ({_viewModel.SelectedMap?.Id ?? "none"})\n" +
                $"日志文件: {snapshot.LatestLogFileName ?? "未发现"}\n" +
                $"战局开始: {snapshot.GameStartAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}");
        }
        if (mapChanged)
        {
            ResetMapTransform();
            UpdateTaskConfiguration();
        }

        var mapEvidenceAuthoritative = IsMapEvidenceAuthoritative(snapshot);
        var coordinateBelongsToCurrentRaid = IsCoordinateFromCurrentRaid(snapshot);
        var acceptedCoordinate = mapEvidenceAuthoritative && coordinateBelongsToCurrentRaid
            ? snapshot.Coordinate
            : null;
        var coordinateMapId = acceptedCoordinate is not null
            ? ResolveCoordinateMapId(snapshot, acceptedCoordinate)
            : snapshot.MapKey;
        var weakRouteCorrectedByManualMap = acceptedCoordinate is not null &&
                                            !string.Equals(coordinateMapId, snapshot.MapKey, StringComparison.OrdinalIgnoreCase);
        bool coordinateChanged;
        if (acceptedCoordinate is not null)
        {
            coordinateChanged = _viewModel.SetLiveCoordinate(acceptedCoordinate, coordinateMapId!);
        }
        else
        {
            // A single incomplete poll must not erase the last confirmed point.
            // When a new raid/map is already authoritative, mark the previous
            // point stale immediately so it remains visible in blue where valid.
            coordinateChanged = mapEvidenceAuthoritative &&
                                !string.IsNullOrWhiteSpace(snapshot.MapKey) &&
                                _viewModel.LiveCoordinate is not null
                ? _viewModel.MarkLiveCoordinateStale()
                : false;
        }
        if (acceptedCoordinate is not null)
            ApplyAutomaticMapLayer(coordinateMapId!, acceptedCoordinate, "截图坐标更新");
        var projectionError = acceptedCoordinate is not null
            ? _viewModel.PlayerMarkerProjectionError
            : null;
        var freshnessChanged = _viewModel.RefreshCoordinateFreshness(DateTimeOffset.Now);
        if (coordinateChanged)
        {
            if (acceptedCoordinate is not null)
            {
                var coordinateDetail =
                    $"地图: {_viewModel.CurrentMapName} ({coordinateMapId})\n文件: {acceptedCoordinate.FileName}\n" +
                    $"坐标: X {acceptedCoordinate.X:0.###} · Y {acceptedCoordinate.Y:0.###} · Z {acceptedCoordinate.Z:0.###}\n" +
                    $"朝向: {acceptedCoordinate.YawDegrees:0.##}°\n截图时间: {acceptedCoordinate.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}\n" +
                    $"文件名时间: {acceptedCoordinate.FileNameTimestamp?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}\n" +
                    $"坐标状态: {(_viewModel.IsCoordinateStale ? "已过期" : "有效")}" +
                    (weakRouteCorrectedByManualMap
                        ? $"\n弱地图证据: {snapshot.MapKey} ({snapshot.MapSource})\n纠正依据: 该坐标无法投影到日志路线地图，但可投影到用户当前地图"
                        : "");
                if (projectionError is null)
                {
                    if (weakRouteCorrectedByManualMap)
                        RuntimeLogService.Warning("地图识别", "已按用户当前地图纠正弱路线证据", coordinateDetail);
                    else
                        RuntimeLogService.Info("坐标", "已更新玩家位置", coordinateDetail);
                    ProcessAirdropCoordinate(coordinateMapId!, acceptedCoordinate);
                }
                else
                {
                    RuntimeLogService.Warning(
                        "坐标",
                        "已读取截图坐标，但无法投影到当前地图",
                        $"{coordinateDetail}\n投影失败: {projectionError}\n地图来源: {snapshot.MapSource ?? "未知"}");
                }
            }
            else if (_viewModel.LiveCoordinate is null)
            {
                RuntimeLogService.Info("坐标", "清除当前玩家位置", $"地图识别键: {snapshot.MapKey ?? "无"}\n原因: 当前截图不属于有效战局或没有可用坐标。");
            }
            else
            {
                RuntimeLogService.Info(
                    "坐标",
                    "保留最后一次已确认位置",
                    $"地图识别键: {snapshot.MapKey ?? "无"}\n原因: 当前读取尚未确认新坐标，旧位置已标记为过期。");
            }
        }
        if (freshnessChanged)
        {
            if (_viewModel.IsCoordinateStale)
                RuntimeLogService.Warning("坐标", "玩家坐标已过期", $"当前地图: {_viewModel.CurrentMapName}\n最近坐标: {_viewModel.CurrentMapCoordinate}");
            else
                RuntimeLogService.Info("坐标", "玩家坐标恢复有效", $"当前地图: {_viewModel.CurrentMapName}\n最近坐标: {_viewModel.CurrentMapCoordinate}");
        }
        var btrPredictionTick = BtrPointDisplayCheck.IsChecked == true &&
                                snapshot.GameStartAt is not null &&
                                snapshot.RaidEndAt is null &&
                                BtrPredictionAdapter.SupportsMap(_viewModel.SelectedMap) &&
                                string.Equals(snapshot.MapKey, _viewModel.SelectedMap?.Id, StringComparison.OrdinalIgnoreCase);
        if (mapChanged || coordinateChanged || freshnessChanged || btrPredictionTick) UpdateMarkers();
        if (_teamSyncFeature?.IsRunning == true) _ = _teamSyncFeature.PublishCurrentLocationAsync();

        if (!snapshot.HasConfiguredPaths)
        {
            MonitorStatusText.Text = "运行状态：未配置路径";
        }
        else if (!IsMapEvidenceAuthoritative(snapshot))
        {
            MonitorStatusText.Text = snapshot.IsTransitDestinationPending
                ? "运行状态：等待目标地图日志"
                : "运行状态：正在读取历史日志";
        }
        else if (snapshot.MapKey is not null)
        {
            MonitorStatusText.Text = acceptedCoordinate is null
                ? $"运行状态：{(snapshot.Coordinate is null ? snapshot.MapDetectionDetail ?? "等待坐标截图" : "等待本局的新坐标截图")}"
                : projectionError is not null
                    ? "运行状态：已读取坐标，但坐标超出当前地图范围"
                : _viewModel.IsCoordinateStale
                    ? "运行状态：坐标已过期，等待新截图"
                    : $"运行状态：{acceptedCoordinate.FileName}";
        }
        else
        {
            MonitorStatusText.Text = acceptedCoordinate is null
                ? $"运行状态：{snapshot.MapDetectionDetail ?? "等待日志与截图"}"
                : _viewModel.IsCoordinateStale
                    ? "运行状态：坐标已过期，等待新截图"
                    : $"运行状态：{acceptedCoordinate.FileName}";
        }

        if (_activePage == WorkspacePage.Settings) UpdateSettingsPage();
    }

    private string? ResolveCoordinateMapId(LocalRaidSnapshot snapshot, LiveCoordinate coordinate)
    {
        if (string.IsNullOrWhiteSpace(snapshot.MapKey) ||
            string.IsNullOrWhiteSpace(snapshot.MapSource) ||
            !snapshot.MapSource.StartsWith("日志路线 ", StringComparison.OrdinalIgnoreCase))
            return snapshot.MapKey;

        var selectedMap = _viewModel.SelectedMap;
        if (selectedMap?.WorldBounds is not { } selectedBounds ||
            string.Equals(selectedMap.Id, snapshot.MapKey, StringComparison.OrdinalIgnoreCase))
            return snapshot.MapKey;

        var detectedMap = _viewModel.Maps.FirstOrDefault(map =>
            string.Equals(map.Id, snapshot.MapKey, StringComparison.OrdinalIgnoreCase));
        if (detectedMap?.WorldBounds is not { } detectedBounds ||
            detectedBounds.Contains(coordinate.X, coordinate.Z))
            return snapshot.MapKey;

        return selectedBounds.Contains(coordinate.X, coordinate.Z)
            ? selectedMap.Id
            : snapshot.MapKey;
    }

    private static string? BuildRaidMapSignature(LocalRaidSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.MapKey)) return null;

        return string.Join(
            "|",
            snapshot.GameLogDirectory ?? "",
            snapshot.LatestLogFileName ?? "",
            snapshot.MapKey.Trim().ToLowerInvariant(),
            snapshot.MapSource ?? "",
            snapshot.GameStartAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "",
            snapshot.MapObservedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    private static bool IsMapEvidenceAuthoritative(LocalRaidSnapshot snapshot) =>
        snapshot.IsMapLogCaughtUp && !snapshot.IsTransitDestinationPending;

    private static string BuildRaidSnapshotDetail(LocalRaidSnapshot snapshot, TimeSpan elapsed)
    {
        var coordinate = snapshot.Coordinate;
        TimeSpan? coordinateAge = coordinate is null ? null : DateTimeOffset.Now - coordinate.CapturedAt;
        return $"耗时: {elapsed.TotalMilliseconds:N0} ms\n" +
               $"路径已配置: {snapshot.HasConfiguredPaths}\n" +
               $"截图目录: {snapshot.ScreenshotDirectory ?? "未配置"}\n" +
               $"游戏日志目录: {snapshot.GameLogDirectory ?? "未配置"}\n" +
               $"最新日志: {snapshot.LatestLogFileName ?? "未发现"}\n" +
               $"地图键: {snapshot.MapKey ?? "未识别"}\n" +
               $"地图来源: {snapshot.MapSource ?? "无"}\n" +
               $"识别详情: {snapshot.MapDetectionDetail ?? "无"}\n" +
               $"地图识别时间: {snapshot.MapObservedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未知"}\n" +
               $"战局开始: {snapshot.GameStartAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未知"}\n" +
               $"战局结束: {snapshot.RaidEndAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未结束或未知"}\n" +
               $"坐标文件: {coordinate?.FileName ?? "未发现"}\n" +
               $"坐标值: {(coordinate is null ? "无" : $"X {coordinate.X:0.###} · Y {coordinate.Y:0.###} · Z {coordinate.Z:0.###} · 朝向 {coordinate.YawDegrees:0.##}°")}\n" +
               $"截图时间: {coordinate?.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未知"}\n" +
               $"文件名时间: {coordinate?.FileNameTimestamp?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}\n" +
               $"截图年龄: {(coordinateAge is null ? "未知" : $"{coordinateAge.Value.TotalSeconds:0.0} 秒")}\n" +
               $"属于当前战局: {IsCoordinateFromCurrentRaid(snapshot)}\n" +
               $"读取状态: {snapshot.StatusMessage}";
    }

    private static string BuildRaidTraceSignature(LocalRaidSnapshot snapshot) => string.Join(
        "|",
        snapshot.MapKey ?? "",
        snapshot.LatestLogFileName ?? "",
        snapshot.Coordinate?.FileName ?? "",
        snapshot.MapObservedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "",
        snapshot.RaidEndAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "",
        snapshot.StatusMessage,
        snapshot.MapDetectionDetail ?? "");

    internal static bool IsCoordinateFromCurrentRaid(LocalRaidSnapshot snapshot)
    {
        if (snapshot.Coordinate is not { } coordinate || string.IsNullOrWhiteSpace(snapshot.MapKey) || snapshot.RaidEndAt is not null)
            return false;
        if (!LocalRaidMonitorService.IsPlausibleCoordinateTime(coordinate.CapturedAt, DateTimeOffset.Now))
            return false;

        // A map transfer stays in the same raid.  GameStarted therefore only
        // filters screenshots from an older raid; MapObservedAt additionally
        // filters the cached screenshot from the map that was just left.  Keep a
        // bounded write-time tolerance because the screenshot file and
        // application log are written by different pipelines and can become
        // visible several seconds apart.
        var earliestAccepted = snapshot.GameStartAt is { } gameStart
            ? gameStart - RaidStartCoordinateTolerance
            : DateTimeOffset.MinValue;
        if (snapshot.MapObservedAt is { } mapObserved)
            earliestAccepted = Max(earliestAccepted, mapObserved - MapEvidenceCoordinateTolerance);
        return coordinate.CapturedAt >= earliestAccepted;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private IReadOnlyList<TaskMarkerGroup> CurrentTaskGroups()
    {
        var mapKey = _viewModel.SelectedMap?.Id;
        return mapKey is not null && _taskPointLoad.GroupsByMap.TryGetValue(mapKey, out var groups)
            ? groups
            : [];
    }

    private IEnumerable<MapMarker> GetSelectedTaskMarkers(bool requireDisplayToggle = true)
    {
        var map = _viewModel.SelectedMap;
        if ((requireDisplayToggle && TaskPointDisplayCheck.IsChecked != true) || map?.WorldBounds is not { } bounds) yield break;

        foreach (var task in CurrentTaskGroups())
        {
            if (!_selectedTaskKeys.Contains(task.SelectionKey) || _completedTaskKeys.Contains(task.SelectionKey)) continue;
            var projectedPoints = new HashSet<(long X, long Y)>();
            foreach (var point in task.Points)
            {
                if (!bounds.TryProject(point.X, point.Z, out var x, out var y)) continue;
                var coordinateKey = ((long)Math.Round(x * 100_000), (long)Math.Round(y * 100_000));
                if (!projectedPoints.Add(coordinateKey)) continue;

                yield return new MapMarker
                {
                    Type = "task",
                    Label = task.TaskName,
                    ToolTipText = string.IsNullOrWhiteSpace(point.Objective)
                        ? task.Detail
                        : $"{task.TaskName}\n{point.Objective}",
                    X = x,
                    Y = y,
                    ShowLabel = true,
                    WorldX = point.X,
                    WorldHeight = point.Y,
                    WorldZ = point.Z
                };
            }
        }
    }

    private void UpdateTaskConfiguration()
    {
        if (TaskListPanel is null) return;

        var tasks = CurrentTaskGroups();
        var searchTerm = TaskSearchBox?.Text.Trim() ?? "";
        var orderedTasks = tasks
            .OrderBy(task => _completedTaskKeys.Contains(task.SelectionKey) ? 1 : 0)
            .ThenByDescending(task => _selectedTaskKeys.Contains(task.SelectionKey))
            .ThenBy(task => task.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var visibleTasks = string.IsNullOrWhiteSpace(searchTerm)
            ? orderedTasks
            : orderedTasks.Where(task => task.TaskName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) || task.Objectives.Any(objective => objective.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))).ToArray();
        TaskListPanel.Children.Clear();
        TaskMapNameText.Text = _viewModel.SelectedMap is null ? "当前地图任务" : $"{_viewModel.CurrentMapName} · 任务点位";
        if (!_taskPointLoad.IsAvailable)
        {
            TaskSelectionSummaryText.Text = "任务点位数据不可用";
            TaskListPanel.Children.Add(new TextBlock { Text = _taskPointLoad.ErrorMessage ?? "未找到任务点位数据", Foreground = (Brush)FindResource("TextFaintBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
            return;
        }

        var selectedCount = tasks.Count(task => _selectedTaskKeys.Contains(task.SelectionKey) && !_completedTaskKeys.Contains(task.SelectionKey));
        var completedCount = tasks.Count(task => _completedTaskKeys.Contains(task.SelectionKey));
        TaskSelectionSummaryText.Text = selectedCount == 0
            ? $"默认不显示 · 当前地图 {tasks.Count} 个任务可选 · 已完成 {completedCount}"
            : $"已选择 {selectedCount} / {tasks.Count} 个任务 · 已完成 {completedCount}（不显示点位）";

        if (tasks.Count == 0)
        {
            TaskListPanel.Children.Add(new TextBlock { Text = "未找到当前地图的任务点数据", Foreground = (Brush)FindResource("TextFaintBrush"), FontSize = 11, Margin = new Thickness(0, 7, 0, 0) });
            return;
        }

        if (!visibleTasks.Any())
        {
            TaskListPanel.Children.Add(new TextBlock { Text = "没有匹配的任务", Foreground = (Brush)FindResource("TextFaintBrush"), FontSize = 11, Margin = new Thickness(0, 7, 0, 0) });
            return;
        }

        foreach (var task in visibleTasks)
        {
            var isCompleted = _completedTaskKeys.Contains(task.SelectionKey);
            var nameText = new TextBlock
            {
                Text = task.TaskName,
                Foreground = isCompleted ? (Brush)FindResource("TextFaintBrush") : (Brush)FindResource("TextBrush"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (isCompleted) nameText.TextDecorations = TextDecorations.Strikethrough;

            var checkBox = new CheckBox
            {
                IsChecked = _selectedTaskKeys.Contains(task.SelectionKey) && !isCompleted,
                IsEnabled = !isCompleted,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 1, 5, 1),
                ToolTip = task.Detail,
                Content = new StackPanel
                {
                    Margin = new Thickness(4, 0, 0, 0),
                    Children =
                    {
                        nameText,
                        new TextBlock { Text = $"{task.Points.Count} 个点 · {task.Detail}", Foreground = (Brush)FindResource("TextFaintBrush"), FontSize = 9, TextWrapping = TextWrapping.Wrap, MaxWidth = 165, Margin = new Thickness(0, 2, 0, 4) }
                    }
                }
            };
            checkBox.Checked += (_, _) => SetTaskSelected(task, true);
            checkBox.Unchecked += (_, _) => SetTaskSelected(task, false);

            var completeButton = new Button
            {
                Content = isCompleted ? "恢复" : "完成",
                Style = (Style)FindResource("TacticalButton"),
                Foreground = isCompleted ? (Brush)FindResource("GreenBrush") : (Brush)FindResource("AmberBrush"),
                Padding = new Thickness(7, 3, 7, 3),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = isCompleted ? "恢复为未完成任务" : "标记完成并隐藏该任务点位"
            };
            completeButton.Click += (_, _) => ToggleTaskCompletion(task);

            var taskRow = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            taskRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            taskRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(checkBox, 0);
            Grid.SetColumn(completeButton, 1);
            taskRow.Children.Add(checkBox);
            taskRow.Children.Add(completeButton);
            TaskListPanel.Children.Add(taskRow);
        }
    }

    private void SetTaskSelected(TaskMarkerGroup task, bool isSelected)
    {
        if (isSelected)
        {
            _completedTaskKeys.Remove(task.SelectionKey);
            _selectedTaskKeys.Add(task.SelectionKey);
        }
        else _selectedTaskKeys.Remove(task.SelectionKey);
        SaveTaskPreferences();
        UpdateTaskConfiguration();
        UpdateMarkers();
    }

    private void ClearTaskSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var task in CurrentTaskGroups()) _selectedTaskKeys.Remove(task.SelectionKey);
        SaveTaskPreferences();
        UpdateTaskConfiguration();
        UpdateMarkers();
    }

    private void ToggleTaskCompletion(TaskMarkerGroup task)
    {
        if (_completedTaskKeys.Contains(task.SelectionKey))
        {
            _completedTaskKeys.Remove(task.SelectionKey);
        }
        else
        {
            _completedTaskKeys.Add(task.SelectionKey);
            _selectedTaskKeys.Remove(task.SelectionKey);
        }

        SaveTaskPreferences();
        UpdateTaskConfiguration();
        UpdateMarkers();
    }

    internal void ApplyTaskTrackingPinSelection(bool enabled, IEnumerable<string> taskNames)
    {
        foreach (var selectionKey in _taskTrackingLinkedSelectionKeys)
            _selectedTaskKeys.Remove(selectionKey);
        _taskTrackingLinkedSelectionKeys.Clear();

        if (enabled)
        {
            var normalizedNames = taskNames
                .Select(NormalizeLinkedTaskName)
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var task in _taskPointLoad.GroupsByMap.Values.SelectMany(groups => groups))
            {
                if (!normalizedNames.Contains(NormalizeLinkedTaskName(task.TaskName)) ||
                    _completedTaskKeys.Contains(task.SelectionKey)) continue;
                if (_selectedTaskKeys.Add(task.SelectionKey))
                    _taskTrackingLinkedSelectionKeys.Add(task.SelectionKey);
            }
            if (_taskTrackingLinkedSelectionKeys.Count > 0)
                TaskPointDisplayCheck.IsChecked = true;
        }

        UpdateTaskConfiguration();
        UpdateMarkers();
    }

    private static string NormalizeLinkedTaskName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private void SaveTaskPreferences() => DesktopPreferencesService.SaveTaskState(
        _selectedTaskKeys.Where(key => !_taskTrackingLinkedSelectionKeys.Contains(key)),
        _completedTaskKeys);

    private void TaskSearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTaskConfiguration();

    private void ShowToast(string message)
    {
        _toastAnimationVersion++;
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        if (MotionEnabled)
        {
            var transform = EnsureTranslateTransform(ToastBorder);
            var duration = new Duration(TimeSpan.FromMilliseconds(170));
            ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }, HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ToastBorder.BeginAnimation(OpacityProperty, null);
            ToastBorder.Opacity = 1;
        }
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var version = _toastAnimationVersion;
        if (!MotionEnabled)
        {
            ToastBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var transform = EnsureTranslateTransform(ToastBorder);
        var duration = new Duration(TimeSpan.FromMilliseconds(130));
        var fade = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (version == _toastAnimationVersion && !_toastTimer.IsEnabled)
            {
                ToastBorder.BeginAnimation(OpacityProperty, null);
                ToastBorder.Opacity = 1;
                ToastBorder.Visibility = Visibility.Collapsed;
            }
        };
        ToastBorder.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
