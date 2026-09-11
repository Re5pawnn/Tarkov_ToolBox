using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TarkovAutoShade;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.ScreenFilter.Models;
using TarkovMapLocator.Modules.ScreenFilter.Services;

namespace TarkovMapLocator.Modules.ScreenFilter;

public partial class ScreenFilterView : UserControl, IFeatureViewLifecycle, IFeatureMouseNavigationGuard
{
    private const int MaximumScreenFilterSavedPresetCount = 64;

    private enum ActiveScreenFilterKind { None, GammaPanel, AutoShade }

    private sealed record ScreenFilterDisplayOption(string DisplayName, string Label);
    private sealed record AutoShadePresetValues(
        int Shadow,
        int Highlight,
        int Color,
        int Indoor,
        int Guard,
        int Black,
        int Strength,
        int Exposure,
        int Contrast,
        int Warmth,
        int Saturation);

    private readonly IFeatureHost _host;
    private readonly GlobalHotkeyService _screenFilterHotkeyService = new();
    private readonly GlobalHotkeyService _autoShadeAnalysisHotkeyService = new();
    private readonly ScreenGammaService _screenGammaService = new();
    private readonly IReadOnlyList<ScreenFilterPresetOption> _screenFilterPresetOptions =
    [
        new("default", "默认", ScreenFilterPreset.Uniform(100, 0, 100)),
        new("day-clear", "白天晴天", ScreenFilterPreset.Uniform(130, 6, 104)),
        new("day-cloudy", "白天阴天", ScreenFilterPreset.Uniform(155, 55, 121)),
        new("night", "极致夜晚", ScreenFilterPreset.Uniform(290, 100, 137)),
        new("custom", "自定义", ScreenFilterPreset.Default)
    ];
    private readonly List<ScreenFilterDisplayOption> _screenFilterDisplayOptions = [];
    private readonly List<ScreenFilterSavedPreset> _screenFilterSavedPresets = [];
    private ScreenFilterRequest _screenFilterRequest = ScreenFilterRequest.Default;
    private bool _suppressScreenFilterChange;
    private bool _screenFilterControlsReady;
    private bool _screenFilterPreviewPending;
    private bool _isCapturingScreenFilterHotkey;
    private readonly ScreenshotWatcher _autoShadeWatcher = new();
    private readonly DispatcherTimer _autoShadePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _autoShadeProcessTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autoShadeCaptureTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _autoShadeSettingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private AppSettings _autoShadeSettings = AppSettings.CreateDefault();
    private AnalysisResult? _autoShadeLastAnalysis;
    private ActiveScreenFilterKind _activeScreenFilterKind;
    private bool _autoShadeControlsReady;
    private bool _suppressAutoShadeChange;
    private bool _autoShadePaused = true;
    private bool _autoShadeProcessSuspended;
    private bool _autoShadeCaptureStarting;
    private bool _autoShadeCapturePending;
    private bool _isCapturingAutoShadeScreenshotHotkey;
    private bool _isCapturingAutoShadeAnalysisHotkey;
    private string? _autoShadeCaptureBaselinePath;
    private DateTime _autoShadeCaptureBaselineWriteTimeUtc;
    private DateTime _autoShadeCaptureStartedAtUtc;
    private int _autoShadeCaptureVersion;
    private int _autoShadeAnalysisVersion;
    private readonly DispatcherTimer _screenFilterSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _screenFilterPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _screenFilterSavePending;
    private Task _screenFilterSaveTask = Task.CompletedTask;
    private bool _autoShadeSettingsSavePending;
    private Task _autoShadeSettingsSaveTask = Task.CompletedTask;
    private bool _hotkeysInitialized;
    private bool _disposed;

    private Window OwnerWindow => Window.GetWindow(this)
        ?? throw new InvalidOperationException("调色组件尚未连接到主窗口。");

    public ScreenFilterView(IFeatureHost host)
    {
        _host = host;
        ScreenFilterModuleContext.Initialize(host);
        InitializeComponent();

        _screenFilterHotkeyService.Pressed += ScreenFilterHotkeyService_Pressed;
        _autoShadeAnalysisHotkeyService.Pressed += AutoShadeAnalysisHotkeyService_Pressed;
        _autoShadeWatcher.ScreenshotReady += AutoShadeWatcher_ScreenshotReady;
        _autoShadeWatcher.WatcherFaulted += AutoShadeWatcher_WatcherFaulted;
        _screenFilterSaveTimer.Tick += (_, _) => PersistPendingScreenFilterRequest();
        _screenFilterPreviewTimer.Tick += (_, _) => ApplyPendingScreenFilterPreview();
        _autoShadePreviewTimer.Tick += (_, _) => ApplyPendingAutoShadePreview();
        _autoShadeProcessTimer.Tick += AutoShadeProcessTimer_Tick;
        _autoShadeCaptureTimeoutTimer.Tick += (_, _) => CancelPendingAutoShadeCapture("截图等待超时");
        _autoShadeSettingsSaveTimer.Tick += (_, _) => PersistPendingAutoShadeSettings();
        Loaded += ScreenFilterView_Loaded;

        InitializeScreenFilterControls();
    }

    public void OnActivated()
    {
        if (_disposed) return;
        RefreshScreenFilterDisplays();
        EnsureHotkeysInitialized();
    }

    public bool ShouldSuppressNavigation(MouseButton button, DependencyObject? source)
    {
        var pressedButton = FindAncestor<Button>(source);
        if (pressedButton == ScreenFilterHotkeyCaptureButton ||
            pressedButton == AutoShadeAnalysisHotkeyCaptureButton ||
            pressedButton == AutoShadeScreenshotHotkeyCaptureButton)
            return true;

        var virtualKey = button == MouseButton.XButton1 ? 0x05 : button == MouseButton.XButton2 ? 0x06 : 0;
        return virtualKey != 0 &&
               (_screenFilterRequest.ToggleHotkey.Normalize().VirtualKey == virtualKey ||
                GetAutoShadeAnalysisHotkey().VirtualKey == virtualKey ||
                GetAutoShadeScreenshotHotkey().VirtualKey == virtualKey);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= ScreenFilterView_Loaded;

        _screenFilterHotkeyService.Pressed -= ScreenFilterHotkeyService_Pressed;
        _screenFilterHotkeyService.Dispose();
        _autoShadeAnalysisHotkeyService.Pressed -= AutoShadeAnalysisHotkeyService_Pressed;
        _autoShadeAnalysisHotkeyService.Dispose();
        _autoShadeAnalysisVersion++;
        _autoShadeCaptureVersion++;
        _autoShadeProcessTimer.Stop();
        _autoShadePreviewTimer.Stop();
        _autoShadeCaptureTimeoutTimer.Stop();
        _autoShadeSettingsSaveTimer.Stop();
        _autoShadeWatcher.ScreenshotReady -= AutoShadeWatcher_ScreenshotReady;
        _autoShadeWatcher.WatcherFaulted -= AutoShadeWatcher_WatcherFaulted;
        _autoShadeWatcher.Dispose();
        _screenFilterSaveTimer.Stop();
        _screenFilterPreviewTimer.Stop();

        if (_screenFilterSavePending)
        {
            _screenFilterSavePending = false;
            _screenFilterSaveTask = ScreenFilterPreferencesService.QueueSaveAsync(_screenFilterRequest);
        }
        try
        {
            _screenFilterSaveTask = ScreenFilterPreferencesService.FlushAsync();
            await _screenFilterSaveTask;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Warning("屏幕调色", "关闭时等待调色配置落盘失败", exception.Message);
        }

        if (_autoShadeSettingsSavePending)
        {
            _autoShadeSettingsSavePending = false;
            _autoShadeSettingsSaveTask = SettingsStore.QueueSaveAsync(_autoShadeSettings);
        }
        try
        {
            _autoShadeSettingsSaveTask = SettingsStore.FlushAsync();
            await _autoShadeSettingsSaveTask;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Warning("自动滤镜", "关闭时等待自动滤镜配置落盘失败", exception.Message);
        }

        try { _screenGammaService.Dispose(); }
        catch { }
    }

    private void ScreenFilterView_Loaded(object sender, RoutedEventArgs e) => EnsureHotkeysInitialized();

    private void EnsureHotkeysInitialized()
    {
        if (_hotkeysInitialized || !IsLoaded) return;
        _hotkeysInitialized = true;
        InitializeScreenFilterHotkey();
        InitializeAutoShadeAnalysisHotkey();
    }

    private void InitializeScreenFilterControls()
    {
        _screenFilterRequest = ScreenFilterPreferencesService.Load();
        InitializeAutoShadeControls();
        _screenFilterSavedPresets.AddRange(ScreenFilterPresetService.Load());
        ScreenFilterPresetComboBox.ItemsSource = _screenFilterPresetOptions;
        RefreshScreenFilterSavedPresetComboBox();
        RefreshScreenFilterDisplays();
        ApplyScreenFilterRequest(_screenFilterRequest, "屏幕调色: 参数已加载，调整后实时生效");
        _screenFilterControlsReady = true;
        UpdateScreenFilterModeUi();
        ConfigureAutoShadeRuntime();
        UpdateScreenFilterHotkeyUi();
    }

    private void InitializeAutoShadeControls()
    {
        _autoShadeSettings = SettingsStore.Load();
        if (!Directory.Exists(_autoShadeSettings.ScreenshotFolder))
        {
            var detectedFolder = ScreenshotFolderLocator.Find();
            if (!string.IsNullOrWhiteSpace(detectedFolder))
                _autoShadeSettings.ScreenshotFolder = detectedFolder;
        }

        _suppressAutoShadeChange = true;
        try
        {
            AutoShadeScreenshotFolderBox.Text = _autoShadeSettings.ScreenshotFolder;
            AutoShadeSmoothCheckBox.IsChecked = _autoShadeSettings.SmoothTransition;
            AutoShadeProcessWatchCheckBox.IsChecked = _autoShadeSettings.ProcessWatchEnabled;
            AutoShadePresetComboBox.SelectedIndex = Math.Clamp(_autoShadeSettings.PresetIndex, 0, 5);
            SetAutoShadeSlidersFromSettings(_autoShadeSettings);
            UpdateAutoShadeValueLabels();
            UpdateAutoShadeAnalysisHotkeyUi();
            UpdateAutoShadeScreenshotHotkeyUi();
        }
        finally
        {
            _suppressAutoShadeChange = false;
        }

        _autoShadeControlsReady = true;
        _autoShadeSettings.AutoWatch = false;
        _autoShadeWatcher.SetFolder(_autoShadeSettings.ScreenshotFolder);
        _autoShadeWatcher.Enabled = false;
    }

    private ScreenFilterMode GetSelectedScreenFilterMode() =>
        ScreenFilterModeComboBox.SelectedIndex == 1
            ? ScreenFilterMode.AutoShade
            : ScreenFilterMode.GammaPanel;

    private void ScreenFilterModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_screenFilterControlsReady || _suppressScreenFilterChange) return;

        _screenFilterPreviewTimer.Stop();
        _screenFilterPreviewPending = false;
        _autoShadePreviewTimer.Stop();
        CancelPendingAutoShadeCapture(null);
        ResetActiveScreenFilter("切换滤镜");
        _autoShadePaused = true;
        SaveScreenFilterRequest();
        UpdateScreenFilterModeUi();
        ConfigureAutoShadeRuntime();
        if (IsLoaded) InitializeAutoShadeAnalysisHotkey();
        if (GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade)
            AutoShadeStatusText.Text = "等待开关热键";
        else
            ScreenFilterStatusText.Text = "屏幕调色: 参数变化后实时生效";
        UpdateScreenFilterHotkeyUi();
    }

    private void UpdateScreenFilterModeUi()
    {
        if (ScreenFilterManualPanel is null || AutoShadePanel is null) return;
        var autoShade = GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade;
        ScreenFilterManualPanel.Visibility = autoShade ? Visibility.Collapsed : Visibility.Visible;
        AutoShadePanel.Visibility = autoShade ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureAutoShadeRuntime()
    {
        if (!_autoShadeControlsReady) return;
        var activeMode = GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade && !_autoShadePaused;
        _autoShadeWatcher.Enabled = activeMode && _autoShadeCapturePending;
        if (activeMode && _autoShadeSettings.ProcessWatchEnabled)
            _autoShadeProcessTimer.Start();
        else
        {
            _autoShadeProcessTimer.Stop();
            _autoShadeProcessSuspended = false;
        }
        AutoShadeToggleButton.Content = _autoShadePaused ? "启用自动滤镜" : "暂停自动滤镜";
        AutoShadeAnalyzeButton.IsEnabled = !_autoShadePaused && !_autoShadeCaptureStarting && !_autoShadeCapturePending;
    }

    private void SetAutoShadeSlidersFromSettings(AppSettings settings)
    {
        AutoShadeShadowSlider.Value = settings.ShadowTarget;
        AutoShadeHighlightSlider.Value = settings.HighlightProtection;
        AutoShadeColorSlider.Value = settings.ColorCorrection;
        AutoShadeIndoorSlider.Value = settings.IndoorComfort;
        AutoShadeGuardSlider.Value = settings.SceneGuard;
        AutoShadeBlackSlider.Value = settings.BlackPoint;
        AutoShadeStrengthSlider.Value = settings.MaxStrength;
        AutoShadeExposureSlider.Value = settings.ExposureBias;
        AutoShadeContrastSlider.Value = settings.ContrastBias;
        AutoShadeWarmthSlider.Value = settings.Warmth;
        AutoShadeSaturationSlider.Value = settings.SaturationBias;
    }

    private static AutoShadePresetValues GetAutoShadeBuiltInPreset(int index) => index switch
    {
        1 => new(55, 86, 52, 76, 91, 50, 64, -1, -2, -1, -1),
        2 => new(74, 90, 78, 96, 92, 54, 74, 2, 1, 0, 1),
        3 => new(58, 97, 58, 90, 100, 44, 64, 1, 2, -2, 0),
        4 => new(62, 100, 68, 72, 100, 52, 70, -2, -1, -1, -2),
        _ => new(70, 76, 72, 72, 88, 56, 82, 0, 0, 0, 0)
    };

    private void ApplyAutoShadePresetValues(AutoShadePresetValues values)
    {
        AutoShadeShadowSlider.Value = values.Shadow;
        AutoShadeHighlightSlider.Value = values.Highlight;
        AutoShadeColorSlider.Value = values.Color;
        AutoShadeIndoorSlider.Value = values.Indoor;
        AutoShadeGuardSlider.Value = values.Guard;
        AutoShadeBlackSlider.Value = values.Black;
        AutoShadeStrengthSlider.Value = values.Strength;
        AutoShadeExposureSlider.Value = values.Exposure;
        AutoShadeContrastSlider.Value = values.Contrast;
        AutoShadeWarmthSlider.Value = values.Warmth;
        AutoShadeSaturationSlider.Value = values.Saturation;
    }

    private void ApplyAutoShadeCustomPresetValues()
    {
        ApplyAutoShadePresetValues(new AutoShadePresetValues(
            _autoShadeSettings.CustomShadowTarget,
            _autoShadeSettings.CustomHighlightProtection,
            _autoShadeSettings.CustomColorCorrection,
            _autoShadeSettings.CustomIndoorComfort,
            _autoShadeSettings.CustomSceneGuard,
            _autoShadeSettings.CustomBlackPoint,
            _autoShadeSettings.CustomMaxStrength,
            _autoShadeSettings.CustomExposureBias,
            _autoShadeSettings.CustomContrastBias,
            _autoShadeSettings.CustomWarmth,
            _autoShadeSettings.CustomSaturationBias));
    }

    private void AutoShadePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_autoShadeControlsReady || _suppressAutoShadeChange || AutoShadePresetComboBox.SelectedIndex < 0) return;

        _suppressAutoShadeChange = true;
        try
        {
            if (AutoShadePresetComboBox.SelectedIndex == 5)
                ApplyAutoShadeCustomPresetValues();
            else
                ApplyAutoShadePresetValues(GetAutoShadeBuiltInPreset(AutoShadePresetComboBox.SelectedIndex));
            UpdateAutoShadeValueLabels();
        }
        finally
        {
            _suppressAutoShadeChange = false;
        }

        SaveAutoShadeSettings();
        ScheduleAutoShadePreview();
    }

    private void AutoShadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_autoShadeControlsReady || _suppressAutoShadeChange) return;

        _suppressAutoShadeChange = true;
        try
        {
            UpdateAutoShadeValueLabels();
            if (AutoShadePresetComboBox.SelectedIndex != 5)
                AutoShadePresetComboBox.SelectedIndex = 5;
        }
        finally
        {
            _suppressAutoShadeChange = false;
        }

        SaveAutoShadeSettings();
        ScheduleAutoShadePreview();
    }

    private void AutoShadeOptionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_autoShadeControlsReady || _suppressAutoShadeChange) return;
        SaveAutoShadeSettings();
        ConfigureAutoShadeRuntime();
        AutoShadeProcessTimer_Tick(this, EventArgs.Empty);
        if (GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade && !_autoShadePaused &&
            !_autoShadeSettings.ProcessWatchEnabled && _autoShadeLastAnalysis?.IsUsable == true &&
            _activeScreenFilterKind != ActiveScreenFilterKind.AutoShade)
            ApplyAutoShadeRecommendation(_autoShadeLastAnalysis);
    }

    private void UpdateAutoShadeValueLabels()
    {
        AutoShadeShadowValue.Text = ((int)Math.Round(AutoShadeShadowSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeHighlightValue.Text = ((int)Math.Round(AutoShadeHighlightSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeColorValue.Text = ((int)Math.Round(AutoShadeColorSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeIndoorValue.Text = ((int)Math.Round(AutoShadeIndoorSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeGuardValue.Text = ((int)Math.Round(AutoShadeGuardSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeBlackValue.Text = ((int)Math.Round(AutoShadeBlackSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeStrengthValue.Text = ((int)Math.Round(AutoShadeStrengthSlider.Value)).ToString(CultureInfo.CurrentCulture);
        AutoShadeExposureValue.Text = FormatSignedAutoShadeValue(AutoShadeExposureSlider.Value);
        AutoShadeContrastValue.Text = FormatSignedAutoShadeValue(AutoShadeContrastSlider.Value);
        AutoShadeWarmthValue.Text = FormatSignedAutoShadeValue(AutoShadeWarmthSlider.Value);
        AutoShadeSaturationValue.Text = FormatSignedAutoShadeValue(AutoShadeSaturationSlider.Value);
    }

    private static string FormatSignedAutoShadeValue(double value)
    {
        var rounded = (int)Math.Round(value);
        return rounded > 0 ? $"+{rounded}" : rounded.ToString(CultureInfo.CurrentCulture);
    }

    private void CaptureAutoShadeSettingsFromControls()
    {
        _autoShadeSettings.ShadowTarget = (int)Math.Round(AutoShadeShadowSlider.Value);
        _autoShadeSettings.HighlightProtection = (int)Math.Round(AutoShadeHighlightSlider.Value);
        _autoShadeSettings.ColorCorrection = (int)Math.Round(AutoShadeColorSlider.Value);
        _autoShadeSettings.IndoorComfort = (int)Math.Round(AutoShadeIndoorSlider.Value);
        _autoShadeSettings.SceneGuard = (int)Math.Round(AutoShadeGuardSlider.Value);
        _autoShadeSettings.BlackPoint = (int)Math.Round(AutoShadeBlackSlider.Value);
        _autoShadeSettings.MaxStrength = (int)Math.Round(AutoShadeStrengthSlider.Value);
        _autoShadeSettings.ExposureBias = (int)Math.Round(AutoShadeExposureSlider.Value);
        _autoShadeSettings.ContrastBias = (int)Math.Round(AutoShadeContrastSlider.Value);
        _autoShadeSettings.Warmth = (int)Math.Round(AutoShadeWarmthSlider.Value);
        _autoShadeSettings.SaturationBias = (int)Math.Round(AutoShadeSaturationSlider.Value);
        _autoShadeSettings.AutoWatch = false;
        _autoShadeSettings.SmoothTransition = AutoShadeSmoothCheckBox.IsChecked == true;
        _autoShadeSettings.ProcessWatchEnabled = AutoShadeProcessWatchCheckBox.IsChecked == true;
        _autoShadeSettings.PresetIndex = Math.Clamp(AutoShadePresetComboBox.SelectedIndex, 0, 5);
        if (_autoShadeSettings.PresetIndex == 5)
        {
            _autoShadeSettings.CustomPresetInitialized = true;
            _autoShadeSettings.CustomShadowTarget = _autoShadeSettings.ShadowTarget;
            _autoShadeSettings.CustomHighlightProtection = _autoShadeSettings.HighlightProtection;
            _autoShadeSettings.CustomColorCorrection = _autoShadeSettings.ColorCorrection;
            _autoShadeSettings.CustomIndoorComfort = _autoShadeSettings.IndoorComfort;
            _autoShadeSettings.CustomSceneGuard = _autoShadeSettings.SceneGuard;
            _autoShadeSettings.CustomBlackPoint = _autoShadeSettings.BlackPoint;
            _autoShadeSettings.CustomMaxStrength = _autoShadeSettings.MaxStrength;
            _autoShadeSettings.CustomExposureBias = _autoShadeSettings.ExposureBias;
            _autoShadeSettings.CustomContrastBias = _autoShadeSettings.ContrastBias;
            _autoShadeSettings.CustomWarmth = _autoShadeSettings.Warmth;
            _autoShadeSettings.CustomSaturationBias = _autoShadeSettings.SaturationBias;
        }
        _autoShadeSettings.DisplayDevice = (ScreenFilterDisplayComboBox.SelectedItem as ScreenFilterDisplayOption)?.DisplayName ?? "";
        _autoShadeSettings.Normalize();
    }

    private void SaveAutoShadeSettings()
    {
        CaptureAutoShadeSettingsFromControls();
        _autoShadeSettingsSavePending = true;
        _autoShadeSettingsSaveTimer.Stop();
        _autoShadeSettingsSaveTimer.Start();
    }

    private void PersistPendingAutoShadeSettings()
    {
        _autoShadeSettingsSaveTimer.Stop();
        if (!_autoShadeSettingsSavePending) return;
        _autoShadeSettingsSavePending = false;
        _autoShadeSettingsSaveTask = SettingsStore.QueueSaveAsync(_autoShadeSettings);
    }

    private bool SaveAutoShadeSettingsImmediately()
    {
        _autoShadeSettingsSaveTimer.Stop();
        _autoShadeSettingsSavePending = false;
        return SettingsStore.Save(_autoShadeSettings);
    }

    private GlobalHotkeyGesture GetAutoShadeScreenshotHotkey() =>
        new GlobalHotkeyGesture(
            _autoShadeSettings.HotkeyKeyCode,
            (GlobalHotkeyModifiers)_autoShadeSettings.HotkeyModifiers).Normalize();

    private GlobalHotkeyGesture GetAutoShadeAnalysisHotkey() =>
        new GlobalHotkeyGesture(
            _autoShadeSettings.AnalysisHotkeyKeyCode,
            (GlobalHotkeyModifiers)_autoShadeSettings.AnalysisHotkeyModifiers).Normalize();

    private static bool HotkeysCanConflict(GlobalHotkeyGesture first, GlobalHotkeyGesture second)
    {
        var normalizedFirst = first.Normalize();
        var normalizedSecond = second.Normalize();
        return normalizedFirst.IsConfigured && normalizedSecond.IsConfigured &&
               normalizedFirst.VirtualKey == normalizedSecond.VirtualKey;
    }

    private bool SetAutoShadeAnalysisHotkey(GlobalHotkeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        _autoShadeSettings.AnalysisHotkeyKeyCode = normalized.VirtualKey;
        _autoShadeSettings.AnalysisHotkeyModifiers = (int)normalized.Modifiers;
        var persisted = SaveAutoShadeSettingsImmediately();
        UpdateAutoShadeAnalysisHotkeyUi();
        return persisted;
    }

    private void UpdateAutoShadeAnalysisHotkeyUi()
    {
        if (AutoShadeAnalysisHotkeyCaptureButton is null || AutoShadeAnalysisHotkeyClearButton is null) return;
        var gesture = GetAutoShadeAnalysisHotkey();
        if (!_isCapturingAutoShadeAnalysisHotkey)
            AutoShadeAnalysisHotkeyCaptureButton.Content = GlobalHotkeyService.Format(gesture);
        AutoShadeAnalysisHotkeyClearButton.IsEnabled = gesture.IsConfigured;
    }

    private void AutoShadeAnalysisHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            CancelPendingAutoShadeCapture("已取消截图");
        if (_isCapturingScreenFilterHotkey) CancelScreenFilterHotkeyCapture();
        if (_isCapturingAutoShadeScreenshotHotkey) CancelAutoShadeScreenshotHotkeyCapture();
        _isCapturingAutoShadeAnalysisHotkey = true;
        UnregisterFilterHotkeysForCapture();
        AutoShadeAnalysisHotkeyCaptureButton.Content = "按下分析热键…";
        AutoShadeAnalysisHotkeyCaptureButton.Focus();
        Keyboard.Focus(AutoShadeAnalysisHotkeyCaptureButton);
    }

    private void AutoShadeAnalysisHotkeyCaptureButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingAutoShadeAnalysisHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelAutoShadeAnalysisHotkeyCapture();
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            ClearAutoShadeAnalysisHotkey();
            return;
        }
        if (IsModifierKey(key))
        {
            AutoShadeAnalysisHotkeyCaptureButton.Content = "按下主键…";
            return;
        }
        CommitAutoShadeAnalysisHotkey(new GlobalHotkeyGesture(
            KeyInterop.VirtualKeyFromKey(key),
            GetPressedHotkeyModifiers()));
    }

    private void AutoShadeAnalysisHotkeyCaptureButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturingAutoShadeAnalysisHotkey || !TryCreateMouseSideGesture(e, out var gesture)) return;
        e.Handled = true;
        CommitAutoShadeAnalysisHotkey(gesture);
    }

    private void CommitAutoShadeAnalysisHotkey(GlobalHotkeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsConfigured)
        {
            AutoShadeStatusText.Text = "这个按键不能用作截图分析热键";
            return;
        }
        if (HotkeysCanConflict(normalized, _screenFilterRequest.ToggleHotkey) ||
            HotkeysCanConflict(normalized, GetAutoShadeScreenshotHotkey()))
        {
            AutoShadeStatusText.Text = "三个自动滤镜热键的主键不能相同";
            return;
        }

        var previous = GetAutoShadeAnalysisHotkey();
        _isCapturingAutoShadeAnalysisHotkey = false;
        if (!TryRegisterFilterHotkeys(normalized, out var registrationError))
        {
            TryRegisterFilterHotkeys(previous, out var rollbackError);
            UpdateAutoShadeAnalysisHotkeyUi();
            AutoShadeStatusText.Text = string.IsNullOrWhiteSpace(rollbackError)
                ? $"截图分析热键注册失败: {registrationError}"
                : $"截图分析热键注册失败: {registrationError}；恢复原热键也失败: {rollbackError}";
            RuntimeLogService.Warning("自动滤镜", "截图分析热键注册失败", AutoShadeStatusText.Text);
            return;
        }

        var persisted = SetAutoShadeAnalysisHotkey(normalized);
        AutoShadeStatusText.Text = persisted
            ? $"截图分析热键已设为 {GlobalHotkeyService.Format(normalized)}"
            : $"截图分析热键已设为 {GlobalHotkeyService.Format(normalized)}，但配置写入失败，仅本次运行有效";
        RuntimeLogService.Info(
            "自动滤镜",
            "截图分析热键已更新",
            $"热键: {GlobalHotkeyService.Format(normalized)}\n持久化: {(persisted ? "成功" : "失败")}");
    }

    private void AutoShadeAnalysisHotkeyCaptureButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isCapturingAutoShadeAnalysisHotkey) CancelAutoShadeAnalysisHotkeyCapture();
    }

    private void AutoShadeAnalysisHotkeyClearButton_Click(object sender, RoutedEventArgs e) =>
        ClearAutoShadeAnalysisHotkey();

    private void ClearAutoShadeAnalysisHotkey()
    {
        var wasCapturing = _isCapturingAutoShadeAnalysisHotkey;
        _isCapturingAutoShadeAnalysisHotkey = false;
        _autoShadeAnalysisHotkeyService.Unregister();
        var persisted = SetAutoShadeAnalysisHotkey(GlobalHotkeyGesture.None);
        var restored = !wasCapturing || RestoreFilterHotkeysAfterCapture();
        if (restored)
            AutoShadeStatusText.Text = persisted ? "截图分析热键已清除" : "截图分析热键已清除，但配置写入失败";
    }

    private void CancelAutoShadeAnalysisHotkeyCapture()
    {
        _isCapturingAutoShadeAnalysisHotkey = false;
        RestoreFilterHotkeysAfterCapture();
        UpdateAutoShadeAnalysisHotkeyUi();
    }

    private bool SetAutoShadeScreenshotHotkey(GlobalHotkeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        _autoShadeSettings.HotkeyKeyCode = normalized.VirtualKey;
        _autoShadeSettings.HotkeyModifiers = (int)normalized.Modifiers;
        var persisted = SaveAutoShadeSettingsImmediately();
        UpdateAutoShadeScreenshotHotkeyUi();
        return persisted;
    }

    private void UpdateAutoShadeScreenshotHotkeyUi()
    {
        if (AutoShadeScreenshotHotkeyCaptureButton is null || AutoShadeScreenshotHotkeyClearButton is null) return;
        var gesture = GetAutoShadeScreenshotHotkey();
        if (!_isCapturingAutoShadeScreenshotHotkey)
            AutoShadeScreenshotHotkeyCaptureButton.Content = GlobalHotkeyService.Format(gesture);
        AutoShadeScreenshotHotkeyClearButton.IsEnabled = gesture.IsConfigured;
    }

    private void AutoShadeScreenshotHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            CancelPendingAutoShadeCapture("已取消截图");
        if (_isCapturingScreenFilterHotkey) CancelScreenFilterHotkeyCapture();
        if (_isCapturingAutoShadeAnalysisHotkey) CancelAutoShadeAnalysisHotkeyCapture();
        _isCapturingAutoShadeScreenshotHotkey = true;
        UnregisterFilterHotkeysForCapture();
        AutoShadeScreenshotHotkeyCaptureButton.Content = "按下截图键…";
        AutoShadeScreenshotHotkeyCaptureButton.Focus();
        Keyboard.Focus(AutoShadeScreenshotHotkeyCaptureButton);
    }

    private void AutoShadeScreenshotHotkeyCaptureButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingAutoShadeScreenshotHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelAutoShadeScreenshotHotkeyCapture();
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            ClearAutoShadeScreenshotHotkey();
            return;
        }
        if (IsModifierKey(key))
        {
            AutoShadeScreenshotHotkeyCaptureButton.Content = "按下主键…";
            return;
        }

        CommitAutoShadeScreenshotHotkey(new GlobalHotkeyGesture(
            KeyInterop.VirtualKeyFromKey(key),
            GetPressedHotkeyModifiers()));
    }

    private void AutoShadeScreenshotHotkeyCaptureButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturingAutoShadeScreenshotHotkey || !TryCreateMouseSideGesture(e, out var gesture)) return;
        e.Handled = true;
        CommitAutoShadeScreenshotHotkey(gesture);
    }

    private void CommitAutoShadeScreenshotHotkey(GlobalHotkeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsConfigured)
        {
            AutoShadeStatusText.Text = "这个按键不能用作游戏截图键";
            return;
        }
        if (HotkeysCanConflict(normalized, _screenFilterRequest.ToggleHotkey) ||
            HotkeysCanConflict(normalized, GetAutoShadeAnalysisHotkey()))
        {
            AutoShadeStatusText.Text = "三个自动滤镜热键的主键不能相同";
            return;
        }

        _isCapturingAutoShadeScreenshotHotkey = false;
        var persisted = SetAutoShadeScreenshotHotkey(normalized);
        var restored = RestoreFilterHotkeysAfterCapture();
        if (restored)
        {
            AutoShadeStatusText.Text = persisted
                ? $"游戏截图键已设为 {GlobalHotkeyService.Format(normalized)}"
                : $"游戏截图键已设为 {GlobalHotkeyService.Format(normalized)}，但配置写入失败，仅本次运行有效";
        }
        RuntimeLogService.Info(
            "自动滤镜",
            "游戏截图键已更新",
            $"按键: {GlobalHotkeyService.Format(normalized)}\n持久化: {(persisted ? "成功" : "失败")}");
    }

    private void AutoShadeScreenshotHotkeyCaptureButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isCapturingAutoShadeScreenshotHotkey) CancelAutoShadeScreenshotHotkeyCapture();
    }

    private void AutoShadeScreenshotHotkeyClearButton_Click(object sender, RoutedEventArgs e) =>
        ClearAutoShadeScreenshotHotkey();

    private void ClearAutoShadeScreenshotHotkey()
    {
        if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            CancelPendingAutoShadeCapture(null);
        var wasCapturing = _isCapturingAutoShadeScreenshotHotkey;
        _isCapturingAutoShadeScreenshotHotkey = false;
        var persisted = SetAutoShadeScreenshotHotkey(GlobalHotkeyGesture.None);
        var restored = !wasCapturing || RestoreFilterHotkeysAfterCapture();
        if (restored)
            AutoShadeStatusText.Text = persisted ? "游戏截图键已清除" : "游戏截图键已清除，但配置写入失败";
    }

    private void CancelAutoShadeScreenshotHotkeyCapture()
    {
        _isCapturingAutoShadeScreenshotHotkey = false;
        RestoreFilterHotkeysAfterCapture();
        UpdateAutoShadeScreenshotHotkeyUi();
    }

    private void UnregisterFilterHotkeysForCapture()
    {
        _screenFilterHotkeyService.Unregister();
        _autoShadeAnalysisHotkeyService.Unregister();
    }

    private bool RestoreFilterHotkeysAfterCapture()
    {
        if (TryRegisterFilterHotkeys(GetAutoShadeAnalysisHotkey(), out var error)) return true;
        AutoShadeStatusText.Text = error;
        RuntimeLogService.Warning("自动滤镜", "恢复滤镜热键失败", error);
        return false;
    }

    private bool TryRegisterFilterHotkeys(GlobalHotkeyGesture requestedAnalysisHotkey, out string error)
    {
        var toggleRegistered = _screenFilterHotkeyService.Register(OwnerWindow, _screenFilterRequest.ToggleHotkey);
        var toggleError = _screenFilterHotkeyService.LastError;
        var analysisHotkey = GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade
            ? requestedAnalysisHotkey.Normalize()
            : GlobalHotkeyGesture.None;
        var analysisRegistered = _autoShadeAnalysisHotkeyService.Register(OwnerWindow, analysisHotkey);
        var analysisError = _autoShadeAnalysisHotkeyService.LastError;

        var errors = new List<string>(2);
        if (!toggleRegistered) errors.Add($"滤镜开关热键不可用（错误码 {toggleError}）");
        if (!analysisRegistered) errors.Add($"截图分析热键不可用（错误码 {analysisError}）");
        error = string.Join("；", errors);
        return errors.Count == 0;
    }

    private static bool TryCreateMouseSideGesture(MouseButtonEventArgs e, out GlobalHotkeyGesture gesture)
    {
        var virtualKey = e.ChangedButton switch
        {
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0
        };
        gesture = virtualKey == 0
            ? GlobalHotkeyGesture.None
            : new GlobalHotkeyGesture(virtualKey, GetPressedHotkeyModifiers()).Normalize();
        return gesture.IsConfigured;
    }

    private AppSettings CreateAutoShadeSettingsSnapshot()
    {
        CaptureAutoShadeSettingsFromControls();
        var snapshot = AppSettings.CreateDefault();
        snapshot.AlgorithmVersion = _autoShadeSettings.AlgorithmVersion;
        snapshot.ShadowTarget = _autoShadeSettings.ShadowTarget;
        snapshot.HighlightProtection = _autoShadeSettings.HighlightProtection;
        snapshot.ColorCorrection = _autoShadeSettings.ColorCorrection;
        snapshot.IndoorComfort = _autoShadeSettings.IndoorComfort;
        snapshot.SceneGuard = _autoShadeSettings.SceneGuard;
        snapshot.BlackPoint = _autoShadeSettings.BlackPoint;
        snapshot.MaxStrength = _autoShadeSettings.MaxStrength;
        snapshot.ExposureBias = _autoShadeSettings.ExposureBias;
        snapshot.ContrastBias = _autoShadeSettings.ContrastBias;
        snapshot.Warmth = _autoShadeSettings.Warmth;
        snapshot.SaturationBias = _autoShadeSettings.SaturationBias;
        snapshot.Normalize();
        return snapshot;
    }

    private void AutoShadeChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Escape from Tarkov 截图目录" };
        if (Directory.Exists(_autoShadeSettings.ScreenshotFolder))
            dialog.InitialDirectory = _autoShadeSettings.ScreenshotFolder;
        if (dialog.ShowDialog(OwnerWindow) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        _autoShadeSettings.ScreenshotFolder = dialog.FolderName;
        AutoShadeScreenshotFolderBox.Text = dialog.FolderName;
        _autoShadeWatcher.SetFolder(dialog.FolderName);
        var persisted = SaveAutoShadeSettingsImmediately();
        ConfigureAutoShadeRuntime();
        AutoShadeStatusText.Text = persisted ? "截图目录已更新" : "截图目录已更新，但配置写入失败，仅本次运行有效";
    }

    private void AutoShadeAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoShadePaused)
        {
            AutoShadeStatusText.Text = "请先开启自动滤镜";
            return;
        }
        BeginAutoShadeCapture(GlobalHotkeyGesture.None);
    }

    private void AutoShadeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoShadePaused)
            EnableAutoShade();
        else
            DisableAutoShade("暂停自动滤镜", "自动滤镜已暂停");
        UpdateScreenFilterHotkeyUi();
    }

    private void EnableAutoShade()
    {
        _autoShadePaused = false;
        ConfigureAutoShadeRuntime();
        if (_autoShadeLastAnalysis?.IsUsable == true && _autoShadeLastAnalysis.Recommendation is not null)
        {
            _autoShadeLastAnalysis.Recommendation = ToneCurve.Recommend(
                _autoShadeLastAnalysis,
                CreateAutoShadeSettingsSnapshot());
            ApplyAutoShadeRecommendation(_autoShadeLastAnalysis);
            return;
        }

        AutoShadeStatusText.Text = "自动滤镜模式已开启，等待截图分析";
    }

    private void DisableAutoShade(string reason, string status)
    {
        _autoShadePaused = true;
        if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            CancelPendingAutoShadeCapture(null);
        ConfigureAutoShadeRuntime();
        if (_screenGammaService.HasActiveAdjustments)
            ResetActiveScreenFilter(reason);
        AutoShadeStatusText.Text = status;
    }

    private void AutoShadeResetButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingAutoShadeCapture(null);
        _autoShadePaused = true;
        ConfigureAutoShadeRuntime();
        ResetActiveScreenFilter("恢复系统颜色");
        AutoShadeStatusText.Text = "已恢复系统颜色";
        UpdateScreenFilterHotkeyUi();
    }

    private async void BeginAutoShadeCapture(GlobalHotkeyGesture triggerHotkey)
    {
        if (_autoShadeCaptureStarting || _autoShadeCapturePending || _disposed) return;
        if (GetSelectedScreenFilterMode() != ScreenFilterMode.AutoShade) return;
        if (_autoShadePaused)
        {
            AutoShadeStatusText.Text = "请先开启自动滤镜";
            return;
        }

        var screenshotHotkey = GetAutoShadeScreenshotHotkey();
        if (!screenshotHotkey.IsConfigured)
        {
            AutoShadeStatusText.Text = "请先录入游戏截图键";
            return;
        }
        if (HotkeysCanConflict(screenshotHotkey, _screenFilterRequest.ToggleHotkey))
        {
            AutoShadeStatusText.Text = "游戏截图键不能与滤镜开关热键相同";
            return;
        }
        if (HotkeysCanConflict(screenshotHotkey, GetAutoShadeAnalysisHotkey()))
        {
            AutoShadeStatusText.Text = "游戏截图键不能与截图分析热键相同";
            return;
        }
        if (!Directory.Exists(_autoShadeSettings.ScreenshotFolder))
        {
            AutoShadeStatusText.Text = "截图目录不存在";
            return;
        }
        if (!EftCaptureTargetService.IsGameForeground())
        {
            AutoShadeStatusText.Text = "请先切回游戏";
            return;
        }

        var version = ++_autoShadeCaptureVersion;
        _autoShadeCaptureStarting = true;
        ConfigureAutoShadeRuntime();
        AutoShadeStatusText.Text = triggerHotkey.IsConfigured ? "等待热键释放…" : "准备截图…";
        try
        {
            var released = await KeyboardInputService.WaitForReleaseAsync(
                triggerHotkey,
                TimeSpan.FromSeconds(1.5),
                _host.ShutdownToken);
            if (_disposed || version != _autoShadeCaptureVersion) return;
            if (!released)
            {
                CancelPendingAutoShadeCapture("请松开截图分析热键后重试");
                return;
            }
            if (!EftCaptureTargetService.IsGameForeground())
            {
                CancelPendingAutoShadeCapture("游戏已离开前台");
                return;
            }

            _autoShadeCaptureBaselinePath = _autoShadeWatcher.FindLatest();
            _autoShadeCaptureBaselineWriteTimeUtc = TryGetFileWriteTimeUtc(_autoShadeCaptureBaselinePath);
            _autoShadeCaptureStartedAtUtc = DateTime.UtcNow;
            _autoShadeCaptureStarting = false;
            _autoShadeCapturePending = true;
            ConfigureAutoShadeRuntime();
            _autoShadeCaptureTimeoutTimer.Stop();
            _autoShadeCaptureTimeoutTimer.Start();

            if (!KeyboardInputService.TrySend(screenshotHotkey, out var error))
            {
                CancelPendingAutoShadeCapture($"发送截图键失败: {error}");
                return;
            }

            AutoShadeStatusText.Text = "等待游戏截图…";
            RuntimeLogService.Info(
                "自动滤镜",
                "已触发单次游戏截图",
                $"按键: {GlobalHotkeyService.Format(screenshotHotkey)}\n目录: {_autoShadeSettings.ScreenshotFolder}");
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) CancelPendingAutoShadeCapture("截图已取消");
        }
        catch (Exception exception)
        {
            CancelPendingAutoShadeCapture($"触发截图失败: {exception.Message}");
            RuntimeLogService.Error("自动滤镜", "触发游戏截图失败", exception);
        }
    }

    private void AutoShadeWatcher_ScreenshotReady(string filePath)
    {
        if (_disposed) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !_autoShadeCapturePending ||
                    GetSelectedScreenFilterMode() != ScreenFilterMode.AutoShade ||
                    !IsPendingAutoShadeScreenshot(filePath)) return;

                _autoShadeCapturePending = false;
                _autoShadeCaptureTimeoutTimer.Stop();
                ConfigureAutoShadeRuntime();
                AnalyzeAutoShadePath(filePath, applyWhenReady: true);
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private void AutoShadeWatcher_WatcherFaulted(string message)
    {
        if (_disposed) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                CancelPendingAutoShadeCapture($"监听失败: {message}");
                RuntimeLogService.Warning("自动滤镜", "截图监听失败", message);
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private bool IsPendingAutoShadeScreenshot(string filePath)
    {
        if (!_autoShadeCapturePending || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        var writeTimeUtc = TryGetFileWriteTimeUtc(filePath);
        if (writeTimeUtc < _autoShadeCaptureStartedAtUtc - TimeSpan.FromMilliseconds(500)) return false;
        return !string.Equals(filePath, _autoShadeCaptureBaselinePath, StringComparison.OrdinalIgnoreCase) ||
               writeTimeUtc > _autoShadeCaptureBaselineWriteTimeUtc;
    }

    private static DateTime TryGetFileWriteTimeUtc(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return DateTime.MinValue;
        try { return File.GetLastWriteTimeUtc(filePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    private void CancelPendingAutoShadeCapture(string? status)
    {
        _autoShadeCaptureVersion++;
        _autoShadeCaptureStarting = false;
        _autoShadeCapturePending = false;
        _autoShadeCaptureTimeoutTimer.Stop();
        _autoShadeWatcher.Enabled = false;
        _autoShadeCaptureBaselinePath = null;
        _autoShadeCaptureBaselineWriteTimeUtc = DateTime.MinValue;
        _autoShadeCaptureStartedAtUtc = DateTime.MinValue;
        if (!string.IsNullOrWhiteSpace(status))
            AutoShadeStatusText.Text = status;
        ConfigureAutoShadeRuntime();
        UpdateScreenFilterHotkeyUi();
    }

    private async void AnalyzeAutoShadePath(string filePath, bool applyWhenReady)
    {
        if (!File.Exists(filePath))
        {
            AutoShadeStatusText.Text = "截图文件不存在";
            return;
        }

        var version = ++_autoShadeAnalysisVersion;
        var settings = CreateAutoShadeSettingsSnapshot();
        AutoShadeAnalyzeButton.IsEnabled = false;
        AutoShadeStatusText.Text = "正在分析截图…";
        try
        {
            var result = await Task.Run(() => ImageAnalyzer.Analyze(filePath, settings));
            if (_disposed || version != _autoShadeAnalysisVersion) return;
            if (result.IsUsable)
            {
                // Analysis metrics are independent from user tuning. Rebuild the
                // recommendation on the UI thread from the controls as they exist
                // now, so slider edits made while the PNG was decoding are honored.
                result.Recommendation = ToneCurve.Recommend(result, CreateAutoShadeSettingsSnapshot());
            }
            _autoShadeLastAnalysis = result;
            if (!result.IsUsable || result.Recommendation is null)
            {
                AutoShadeStatusText.Text = $"已跳过: {result.SkipReason}";
                ConfigureAutoShadeRuntime();
                RuntimeLogService.Info("自动滤镜", "截图不适合自动调色", $"文件: {filePath}\n原因: {result.SkipReason}");
                return;
            }

            AutoShadeStatusText.Text = FormatAutoShadeAnalysis(result);
            RuntimeLogService.Info(
                "自动滤镜",
                "截图分析完成",
                $"文件: {filePath}\n场景: {result.SceneLabel}\n方案: {result.Recommendation.ProfileName}\n" +
                $"Gamma: {result.Recommendation.EquivalentGamma:0.00}\n亮度: {result.Recommendation.BrightnessBoost:+0.0;-0.0;0.0}\n" +
                $"对比度: {result.Recommendation.ContrastBoost:+0.0;-0.0;0.0}");
            if (applyWhenReady && !_autoShadePaused)
                ApplyAutoShadeRecommendation(result);
        }
        catch (Exception exception)
        {
            if (version != _autoShadeAnalysisVersion) return;
            AutoShadeStatusText.Text = $"分析失败: {exception.Message}";
            ConfigureAutoShadeRuntime();
            RuntimeLogService.Error("自动滤镜", "截图分析失败", exception, $"文件: {filePath}");
        }
        finally
        {
            if (!_disposed && version == _autoShadeAnalysisVersion)
                AutoShadeAnalyzeButton.IsEnabled = true;
        }
    }

    private static string FormatAutoShadeAnalysis(AnalysisResult result) =>
        $"{result.SceneLabel} · {result.Recommendation.ProfileName} · Gamma {result.Recommendation.EquivalentGamma:0.00} · " +
        $"亮度 {result.Recommendation.BrightnessBoost:+0.0;-0.0;0.0} · 对比 {result.Recommendation.ContrastBoost:+0.0;-0.0;0.0}";

    private void ScheduleAutoShadePreview()
    {
        if (_autoShadeLastAnalysis?.IsUsable != true || GetSelectedScreenFilterMode() != ScreenFilterMode.AutoShade) return;
        _autoShadePreviewTimer.Stop();
        _autoShadePreviewTimer.Start();
    }

    private void ApplyPendingAutoShadePreview()
    {
        _autoShadePreviewTimer.Stop();
        if (_autoShadeLastAnalysis?.IsUsable != true || _autoShadePaused) return;
        _autoShadeLastAnalysis.Recommendation = ToneCurve.Recommend(_autoShadeLastAnalysis, CreateAutoShadeSettingsSnapshot());
        ApplyAutoShadeRecommendation(_autoShadeLastAnalysis);
    }

    private void ApplyAutoShadeRecommendation(AnalysisResult result)
    {
        if (GetSelectedScreenFilterMode() != ScreenFilterMode.AutoShade || _autoShadePaused || result.Recommendation is null) return;
        if (_autoShadeSettings.ProcessWatchEnabled && !EftCaptureTargetService.IsGameForeground())
        {
            _autoShadeProcessSuspended = true;
            AutoShadeStatusText.Text = "等待游戏回到前台";
            UpdateScreenFilterHotkeyUi();
            return;
        }

        try
        {
            var displayName = (ScreenFilterDisplayComboBox.SelectedItem as ScreenFilterDisplayOption)?.DisplayName ?? "";
            var recommendation = result.Recommendation;
            var transition = _autoShadeSettings.SmoothTransition
                ? TimeSpan.FromMilliseconds(280 + Math.Round(recommendation.ChangeStrength * 120.0))
                : TimeSpan.Zero;
            var applied = _screenGammaService.ApplyCurve(
                recommendation.Red,
                recommendation.Green,
                recommendation.Blue,
                displayName,
                transition);
            if (applied.AppliedCount > 0) _activeScreenFilterKind = ActiveScreenFilterKind.AutoShade;
            else ConfigureAutoShadeRuntime();
            AutoShadeStatusText.Text = applied.IsSuccess ? FormatAutoShadeAnalysis(result) : applied.Message;
            UpdateScreenFilterHotkeyUi();
        }
        catch (Exception exception)
        {
            ConfigureAutoShadeRuntime();
            AutoShadeStatusText.Text = $"应用失败: {exception.Message}";
            RuntimeLogService.Error("自动滤镜", "应用自动曲线失败", exception);
            UpdateScreenFilterHotkeyUi();
        }
    }

    private void AutoShadeProcessTimer_Tick(object? sender, EventArgs e)
    {
        if (GetSelectedScreenFilterMode() != ScreenFilterMode.AutoShade || _autoShadePaused || !_autoShadeSettings.ProcessWatchEnabled) return;
        var gameIsForeground = EftCaptureTargetService.IsGameForeground();
        if (!gameIsForeground)
        {
            if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            {
                CancelPendingAutoShadeCapture("游戏已离开前台");
                return;
            }
            if (_activeScreenFilterKind == ActiveScreenFilterKind.AutoShade)
                ResetActiveScreenFilter("游戏离开前台");
            _autoShadeProcessSuspended = true;
            AutoShadeStatusText.Text = "等待游戏回到前台";
            return;
        }

        if (!_autoShadeProcessSuspended) return;
        _autoShadeProcessSuspended = false;
        if (_autoShadeLastAnalysis?.IsUsable == true)
            ApplyAutoShadeRecommendation(_autoShadeLastAnalysis);
        else
            AutoShadeStatusText.Text = "等待新截图";
        UpdateScreenFilterHotkeyUi();
    }

    private void ResetActiveScreenFilter(string reason)
    {
        try
        {
            var result = _screenGammaService.ResetAll();
            if (result.FailedCount == 0) _activeScreenFilterKind = ActiveScreenFilterKind.None;
            RuntimeLogService.Info("屏幕调色", reason, $"结果: {result.Message}");
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("屏幕调色", $"{reason}失败", exception);
        }
    }

    private void InitializeScreenFilterHotkey()
    {
        var hotkey = _screenFilterRequest.ToggleHotkey.Normalize();
        if (_screenFilterHotkeyService.Register(OwnerWindow, hotkey))
        {
            RuntimeLogService.Info(
                "屏幕调色",
                hotkey.IsConfigured ? "滤镜全局热键已启用" : "滤镜全局热键未设置",
                hotkey.IsConfigured ? $"热键: {GlobalHotkeyService.Format(hotkey)}" : null);
        }
        else
        {
            ScreenFilterStatusText.Text = $"屏幕调色: 热键注册失败（错误码 {_screenFilterHotkeyService.LastError}）";
            RuntimeLogService.Warning(
                "屏幕调色",
                "滤镜全局热键注册失败",
                $"热键: {GlobalHotkeyService.Format(hotkey)}\nWin32 错误码: {_screenFilterHotkeyService.LastError}");
        }

        UpdateScreenFilterHotkeyUi();
    }

    private void InitializeAutoShadeAnalysisHotkey()
    {
        var hotkey = GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade
            ? GetAutoShadeAnalysisHotkey()
            : GlobalHotkeyGesture.None;
        if (_autoShadeAnalysisHotkeyService.Register(OwnerWindow, hotkey))
        {
            RuntimeLogService.Info(
                "自动滤镜",
                hotkey.IsConfigured ? "截图分析全局热键已启用" : "截图分析全局热键未启用",
                hotkey.IsConfigured ? $"热键: {GlobalHotkeyService.Format(hotkey)}" : null);
            return;
        }

        AutoShadeStatusText.Text = $"截图分析热键注册失败（错误码 {_autoShadeAnalysisHotkeyService.LastError}）";
        RuntimeLogService.Warning(
            "自动滤镜",
            "截图分析全局热键注册失败",
            $"热键: {GlobalHotkeyService.Format(hotkey)}\nWin32 错误码: {_autoShadeAnalysisHotkeyService.LastError}");
    }

    private void AutoShadeAnalysisHotkeyService_Pressed()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(AutoShadeAnalysisHotkeyService_Pressed));
            return;
        }
        if (GetSelectedScreenFilterMode() != ScreenFilterMode.AutoShade) return;
        if (_autoShadePaused)
        {
            AutoShadeStatusText.Text = "请先开启自动滤镜";
            return;
        }
        if (_autoShadeCaptureStarting || _autoShadeCapturePending)
        {
            AutoShadeStatusText.Text = "正在等待游戏截图";
            return;
        }

        BeginAutoShadeCapture(GetAutoShadeAnalysisHotkey());
    }

    private void RefreshScreenFilterDisplays()
    {
        var selectedDisplayName = _screenFilterControlsReady
            ? CaptureScreenFilterRequest().DisplayName
            : _screenFilterRequest.DisplayName;

        _suppressScreenFilterChange = true;
        try
        {
            var displays = _screenGammaService.GetDisplays();
            _screenFilterDisplayOptions.Clear();
            _screenFilterDisplayOptions.Add(new ScreenFilterDisplayOption("", "全部屏幕"));
            _screenFilterDisplayOptions.AddRange(displays.Select(display => new ScreenFilterDisplayOption(display.DeviceName, display.Label)));
            ScreenFilterDisplayComboBox.ItemsSource = null;
            ScreenFilterDisplayComboBox.ItemsSource = _screenFilterDisplayOptions;
            SelectScreenFilterDisplay(selectedDisplayName);
        }
        catch (Exception exception)
        {
            ScreenFilterStatusText.Text = $"屏幕调色: 读取屏幕失败: {exception.Message}";
        }
        finally
        {
            _suppressScreenFilterChange = false;
        }
    }

    private void ApplyScreenFilterRequest(ScreenFilterRequest request, string? status)
    {
        var normalized = request.Normalize();
        _suppressScreenFilterChange = true;
        try
        {
            ScreenFilterModeComboBox.SelectedIndex = normalized.Mode == ScreenFilterMode.AutoShade ? 1 : 0;
            SelectScreenFilterPreset(normalized.PresetId);
            SelectScreenFilterDisplay(normalized.DisplayName);
            ScreenFilterLinkChannelsCheckBox.IsChecked = normalized.LinkChannels;
            SetScreenFilterValues(normalized.ToPreset());
            UpdateScreenFilterControlVisibility();
            _screenFilterRequest = CaptureScreenFilterRequest();
        }
        finally
        {
            _suppressScreenFilterChange = false;
        }

        if (!string.IsNullOrWhiteSpace(status)) ScreenFilterStatusText.Text = status;
        UpdateScreenFilterModeUi();
        UpdateScreenFilterHotkeyUi();
    }

    private void SetScreenFilterValues(ScreenFilterPreset preset)
    {
        var normalized = preset.Normalize();
        SetScreenFilterChannelValues("red", normalized.Red);
        SetScreenFilterChannelValues("green", normalized.Green);
        SetScreenFilterChannelValues("blue", normalized.Blue);
        SetScreenFilterChannelValues("linked", normalized.Red);
        UpdateScreenFilterValueBoxes();
    }

    private void SetScreenFilterChannelValues(string channel, ScreenFilterChannel values)
    {
        GetScreenFilterSlider($"{channel}-gamma").Value = values.Gamma;
        GetScreenFilterSlider($"{channel}-brightness").Value = values.Brightness;
        GetScreenFilterSlider($"{channel}-contrast").Value = values.Contrast;
    }

    private void UpdateScreenFilterValueBoxes()
    {
        foreach (var tag in ScreenFilterParameterTags)
        {
            GetScreenFilterValueBox(tag).Text = Math.Round(GetScreenFilterSlider(tag).Value).ToString(CultureInfo.CurrentCulture);
        }
    }

    private ScreenFilterRequest CaptureScreenFilterRequest()
    {
        var presetId = (ScreenFilterPresetComboBox.SelectedItem as ScreenFilterPresetOption)?.Id ?? "custom";
        var displayName = (ScreenFilterDisplayComboBox.SelectedItem as ScreenFilterDisplayOption)?.DisplayName ?? "";
        var linkChannels = ScreenFilterLinkChannelsCheckBox.IsChecked == true;
        var red = linkChannels ? CaptureScreenFilterChannel("linked") : CaptureScreenFilterChannel("red");
        return new ScreenFilterRequest
        {
            PresetId = presetId,
            DisplayName = displayName,
            Mode = GetSelectedScreenFilterMode(),
            LinkChannels = linkChannels,
            ToggleHotkey = _screenFilterRequest.ToggleHotkey,
            Red = red,
            Green = linkChannels ? red : CaptureScreenFilterChannel("green"),
            Blue = linkChannels ? red : CaptureScreenFilterChannel("blue")
        }.Normalize();
    }

    private ScreenFilterChannel CaptureScreenFilterChannel(string channel) => new(
        (int)Math.Round(GetScreenFilterSlider($"{channel}-gamma").Value),
        (int)Math.Round(GetScreenFilterSlider($"{channel}-brightness").Value),
        (int)Math.Round(GetScreenFilterSlider($"{channel}-contrast").Value));

    private static readonly string[] ScreenFilterParameterTags =
    [
        "linked-gamma", "linked-brightness", "linked-contrast",
        "red-gamma", "red-brightness", "red-contrast",
        "green-gamma", "green-brightness", "green-contrast",
        "blue-gamma", "blue-brightness", "blue-contrast"
    ];

    private Slider GetScreenFilterSlider(string tag) => tag switch
    {
        "linked-gamma" => ScreenFilterLinkedGammaSlider,
        "linked-brightness" => ScreenFilterLinkedBrightnessSlider,
        "linked-contrast" => ScreenFilterLinkedContrastSlider,
        "red-gamma" => ScreenFilterRedGammaSlider,
        "red-brightness" => ScreenFilterRedBrightnessSlider,
        "red-contrast" => ScreenFilterRedContrastSlider,
        "green-gamma" => ScreenFilterGreenGammaSlider,
        "green-brightness" => ScreenFilterGreenBrightnessSlider,
        "green-contrast" => ScreenFilterGreenContrastSlider,
        "blue-gamma" => ScreenFilterBlueGammaSlider,
        "blue-brightness" => ScreenFilterBlueBrightnessSlider,
        "blue-contrast" => ScreenFilterBlueContrastSlider,
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Unknown screen filter parameter")
    };

    private TextBox GetScreenFilterValueBox(string tag) => tag switch
    {
        "linked-gamma" => ScreenFilterLinkedGammaValueBox,
        "linked-brightness" => ScreenFilterLinkedBrightnessValueBox,
        "linked-contrast" => ScreenFilterLinkedContrastValueBox,
        "red-gamma" => ScreenFilterRedGammaValueBox,
        "red-brightness" => ScreenFilterRedBrightnessValueBox,
        "red-contrast" => ScreenFilterRedContrastValueBox,
        "green-gamma" => ScreenFilterGreenGammaValueBox,
        "green-brightness" => ScreenFilterGreenBrightnessValueBox,
        "green-contrast" => ScreenFilterGreenContrastValueBox,
        "blue-gamma" => ScreenFilterBlueGammaValueBox,
        "blue-brightness" => ScreenFilterBlueBrightnessValueBox,
        "blue-contrast" => ScreenFilterBlueContrastValueBox,
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Unknown screen filter parameter")
    };

    private void SelectScreenFilterPreset(string presetId)
    {
        var normalized = string.IsNullOrWhiteSpace(presetId) ? "default" : presetId.Trim();
        ScreenFilterPresetComboBox.SelectedItem = _screenFilterPresetOptions.FirstOrDefault(
            preset => string.Equals(preset.Id, normalized, StringComparison.OrdinalIgnoreCase)) ?? _screenFilterPresetOptions[0];
    }

    private void SelectScreenFilterDisplay(string displayName)
    {
        var normalized = displayName?.Trim() ?? "";
        ScreenFilterDisplayComboBox.SelectedItem = _screenFilterDisplayOptions.FirstOrDefault(
            display => string.Equals(display.DisplayName, normalized, StringComparison.OrdinalIgnoreCase)) ??
            _screenFilterDisplayOptions.FirstOrDefault();
    }

    private ScreenFilterRequest SaveScreenFilterRequest(string? status = null)
    {
        _screenFilterRequest = CaptureScreenFilterRequest();
        _screenFilterSavePending = true;
        _screenFilterSaveTimer.Stop();
        _screenFilterSaveTimer.Start();
        if (!string.IsNullOrWhiteSpace(status)) ScreenFilterStatusText.Text = status;
        return _screenFilterRequest;
    }

    private void PersistPendingScreenFilterRequest()
    {
        _screenFilterSaveTimer.Stop();
        if (!_screenFilterSavePending) return;
        _screenFilterSavePending = false;
        var request = _screenFilterRequest;
        _screenFilterSaveTask = ScreenFilterPreferencesService.QueueSaveAsync(request);
    }

    private void ScreenFilterPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_screenFilterControlsReady || _suppressScreenFilterChange || ScreenFilterPresetComboBox.SelectedItem is not ScreenFilterPresetOption selectedPreset) return;

        _suppressScreenFilterChange = true;
        try
        {
            if (!string.Equals(selectedPreset.Id, "custom", StringComparison.Ordinal)) SetScreenFilterValues(selectedPreset.Preset);
        }
        finally
        {
            _suppressScreenFilterChange = false;
        }

        SaveScreenFilterRequest("屏幕调色: 正在应用预设…");
        ScheduleScreenFilterPreview();
    }

    private void ScreenFilterDisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_screenFilterControlsReady || _suppressScreenFilterChange) return;
        SaveScreenFilterRequest("屏幕调色: 屏幕已切换");
        if (GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade)
        {
            SaveAutoShadeSettings();
            if (_autoShadeLastAnalysis?.IsUsable == true && !_autoShadePaused)
                ApplyAutoShadeRecommendation(_autoShadeLastAnalysis);
        }
        else
        {
            ScheduleScreenFilterPreview();
        }
    }

    private void ScreenFilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_screenFilterControlsReady || _suppressScreenFilterChange) return;

        _suppressScreenFilterChange = true;
        try
        {
            if (ScreenFilterLinkChannelsCheckBox.IsChecked == true && sender is Slider { Tag: string changedTag })
            {
                LinkScreenFilterParameter(changedTag, e.NewValue);
            }
            UpdateScreenFilterValueBoxes();
            SelectScreenFilterPreset("custom");
        }
        finally
        {
            _suppressScreenFilterChange = false;
        }

        SaveScreenFilterRequest("屏幕调色: 实时预览中…");
        ScheduleScreenFilterPreview();
    }

    private void ScreenFilterLinkChannelsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_screenFilterControlsReady || _suppressScreenFilterChange) return;

        _suppressScreenFilterChange = true;
        try
        {
            if (ScreenFilterLinkChannelsCheckBox.IsChecked == true)
            {
                var source = CaptureScreenFilterChannel("red");
                SetScreenFilterChannelValues("linked", source);
                SetScreenFilterChannelValues("green", source);
                SetScreenFilterChannelValues("blue", source);
            }
            else
            {
                var source = CaptureScreenFilterChannel("linked");
                SetScreenFilterChannelValues("red", source);
                SetScreenFilterChannelValues("green", source);
                SetScreenFilterChannelValues("blue", source);
            }
            UpdateScreenFilterControlVisibility();
            UpdateScreenFilterValueBoxes();
            SelectScreenFilterPreset("custom");
        }
        finally
        {
            _suppressScreenFilterChange = false;
        }

        SaveScreenFilterRequest("屏幕调色: RGB 联动已更新");
        ScheduleScreenFilterPreview();
    }

    private void LinkScreenFilterParameter(string changedTag, double value)
    {
        var separator = changedTag.IndexOf('-');
        if (separator < 0 || separator == changedTag.Length - 1) return;
        var parameter = changedTag[(separator + 1)..];
        GetScreenFilterSlider($"linked-{parameter}").Value = value;
        foreach (var channel in new[] { "red", "green", "blue" })
        {
            GetScreenFilterSlider($"{channel}-{parameter}").Value = value;
        }
    }

    private void UpdateScreenFilterControlVisibility()
    {
        var linked = ScreenFilterLinkChannelsCheckBox.IsChecked == true;
        ScreenFilterLinkedControlPanel.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        ScreenFilterIndependentControlPanel.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        ScreenFilterCurveTitleText.Text = linked ? "总体曲线" : "RGB 独立曲线";
    }

    private void ScreenFilterValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox valueBox) ApplyScreenFilterValueBox(valueBox);
    }

    private void ScreenFilterValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox valueBox) return;
        ApplyScreenFilterValueBox(valueBox);
        e.Handled = true;
    }

    private void ApplyScreenFilterValueBox(TextBox valueBox)
    {
        if (!_screenFilterControlsReady || _suppressScreenFilterChange) return;
        if (!TryReadScreenFilterValue(valueBox.Text, out var value))
        {
            UpdateScreenFilterValueBoxes();
            return;
        }

        _suppressScreenFilterChange = true;
        try
        {
            if (valueBox.Tag is not string tag) return;
            var slider = GetScreenFilterSlider(tag);
            slider.Value = Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);
            if (ScreenFilterLinkChannelsCheckBox.IsChecked == true) LinkScreenFilterParameter(tag, slider.Value);

            SelectScreenFilterPreset("custom");
            UpdateScreenFilterValueBoxes();
        }
        finally
        {
            _suppressScreenFilterChange = false;
        }

        SaveScreenFilterRequest("屏幕调色: 实时预览中…");
        ScheduleScreenFilterPreview();
    }

    private static bool TryReadScreenFilterValue(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void ScreenFilterApplyButton_Click(object sender, RoutedEventArgs e)
    {
        _screenFilterPreviewTimer.Stop();
        _screenFilterPreviewPending = false;
        ApplyScreenFilterNow(SaveScreenFilterRequest(), logChange: true);
    }

    private void ScreenFilterSavedPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScreenFilterSavedPresetComboBox.SelectedItem is ScreenFilterSavedPreset saved &&
            ScreenFilterSavedPresetNameBox is not null)
            ScreenFilterSavedPresetNameBox.Text = saved.Name;
        UpdateScreenFilterSavedPresetButtons();
    }

    private void ScreenFilterSavedPresetSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var requestedName = ScreenFilterSavedPresetNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            ScreenFilterStatusText.Text = "屏幕调色: 请输入配置名称";
            ScreenFilterSavedPresetNameBox.Focus();
            return;
        }
        if (requestedName.Length > 40) requestedName = requestedName[..40];

        var current = CaptureScreenFilterRequest();
        var existing = _screenFilterSavedPresets.FirstOrDefault(preset =>
            string.Equals(preset.Name, requestedName, StringComparison.OrdinalIgnoreCase));
        if (existing is null && _screenFilterSavedPresets.Count >= MaximumScreenFilterSavedPresetCount)
        {
            ScreenFilterStatusText.Text = $"屏幕调色: 最多保存 {MaximumScreenFilterSavedPresetCount} 个配置";
            return;
        }
        var saved = new ScreenFilterSavedPreset(
            existing?.Id ?? Guid.NewGuid().ToString("N"),
            requestedName,
            current.LinkChannels,
            current.Red,
            current.Green,
            current.Blue).Normalize();

        if (existing is null)
        {
            _screenFilterSavedPresets.Add(saved);
        }
        else
        {
            var index = _screenFilterSavedPresets.FindIndex(preset =>
                string.Equals(preset.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _screenFilterSavedPresets[index] = saved;
        }

        var persisted = ScreenFilterPresetService.Save(_screenFilterSavedPresets);
        RefreshScreenFilterSavedPresetComboBox(saved);
        ScreenFilterStatusText.Text = persisted
            ? $"屏幕调色: 已保存配置“{saved.Name}”"
            : $"屏幕调色: 配置“{saved.Name}”仅在本次运行中可用";
        RuntimeLogService.Info(
            "屏幕调色",
            "自定义滤镜配置已保存",
            $"名称: {saved.Name}\nRGB 联动: {(saved.LinkChannels ? "开启" : "关闭")}\n持久化: {(persisted ? "成功" : "失败")}");
    }

    private void ScreenFilterSavedPresetLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenFilterSavedPresetComboBox.SelectedItem is not ScreenFilterSavedPreset saved)
        {
            ScreenFilterStatusText.Text = "屏幕调色: 请先选择配置";
            return;
        }

        var current = CaptureScreenFilterRequest();
        var request = current with
        {
            PresetId = "custom",
            LinkChannels = saved.LinkChannels,
            Red = saved.Red,
            Green = saved.Green,
            Blue = saved.Blue
        };
        ApplyScreenFilterRequest(request, null);
        SaveScreenFilterRequest($"屏幕调色: 已载入配置“{saved.Name}”");
        ScheduleScreenFilterPreview();
        RefreshScreenFilterSavedPresetComboBox(saved);
        RuntimeLogService.Info("屏幕调色", "已载入自定义滤镜配置", $"名称: {saved.Name}");
    }

    private void ScreenFilterSavedPresetDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenFilterSavedPresetComboBox.SelectedItem is not ScreenFilterSavedPreset saved)
        {
            ScreenFilterStatusText.Text = "屏幕调色: 请先选择配置";
            return;
        }

        _screenFilterSavedPresets.RemoveAll(preset =>
            string.Equals(preset.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
        var persisted = ScreenFilterPresetService.Save(_screenFilterSavedPresets);
        RefreshScreenFilterSavedPresetComboBox();
        ScreenFilterStatusText.Text = persisted
            ? $"屏幕调色: 已删除配置“{saved.Name}”"
            : $"屏幕调色: 配置“{saved.Name}”已从当前列表移除，写入磁盘失败";
        RuntimeLogService.Info("屏幕调色", "自定义滤镜配置已删除", $"名称: {saved.Name}\n持久化: {(persisted ? "成功" : "失败")}");
    }

    private void RefreshScreenFilterSavedPresetComboBox(ScreenFilterSavedPreset? preferred = null)
    {
        var selectedId = preferred?.Id ??
            (ScreenFilterSavedPresetComboBox.SelectedItem as ScreenFilterSavedPreset)?.Id;
        var ordered = _screenFilterSavedPresets
            .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        ScreenFilterSavedPresetComboBox.ItemsSource = null;
        ScreenFilterSavedPresetComboBox.ItemsSource = ordered;
        ScreenFilterSavedPresetComboBox.SelectedItem = ordered.FirstOrDefault(preset =>
            string.Equals(preset.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (ScreenFilterSavedPresetComboBox.SelectedItem is null && preferred is null)
            ScreenFilterSavedPresetNameBox.Text = "";
        else if (ScreenFilterSavedPresetComboBox.SelectedItem is ScreenFilterSavedPreset selected)
            ScreenFilterSavedPresetNameBox.Text = selected.Name;
        UpdateScreenFilterSavedPresetButtons();
    }

    private void UpdateScreenFilterSavedPresetButtons()
    {
        if (ScreenFilterSavedPresetSaveButton is null || ScreenFilterSavedPresetLoadButton is null || ScreenFilterSavedPresetDeleteButton is null) return;
        var hasSelection = ScreenFilterSavedPresetComboBox.SelectedItem is ScreenFilterSavedPreset;
        ScreenFilterSavedPresetSaveButton.IsEnabled = true;
        ScreenFilterSavedPresetLoadButton.IsEnabled = hasSelection;
        ScreenFilterSavedPresetDeleteButton.IsEnabled = hasSelection;
    }

    private void ScreenFilterHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            CancelPendingAutoShadeCapture("已取消截图");
        if (_isCapturingAutoShadeScreenshotHotkey) CancelAutoShadeScreenshotHotkeyCapture();
        if (_isCapturingAutoShadeAnalysisHotkey) CancelAutoShadeAnalysisHotkeyCapture();
        _isCapturingScreenFilterHotkey = true;
        UnregisterFilterHotkeysForCapture();
        ScreenFilterHotkeyCaptureButton.Content = "按下热键…";
        ScreenFilterHotkeyCaptureButton.Focus();
        Keyboard.Focus(ScreenFilterHotkeyCaptureButton);
    }

    private void ScreenFilterHotkeyCaptureButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingScreenFilterHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelScreenFilterHotkeyCapture();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            ClearScreenFilterHotkey();
            return;
        }

        if (IsModifierKey(key))
        {
            ScreenFilterHotkeyCaptureButton.Content = "按下主键…";
            return;
        }

        CommitScreenFilterHotkey(new GlobalHotkeyGesture(
            KeyInterop.VirtualKeyFromKey(key),
            GetPressedHotkeyModifiers()));
    }

    private void ScreenFilterHotkeyCaptureButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturingScreenFilterHotkey || !TryCreateMouseSideGesture(e, out var gesture)) return;
        e.Handled = true;
        CommitScreenFilterHotkey(gesture);
    }

    private void CommitScreenFilterHotkey(GlobalHotkeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsConfigured)
        {
            ScreenFilterStatusText.Text = "屏幕调色: 这个按键不能用作热键";
            return;
        }
        if (HotkeysCanConflict(normalized, GetAutoShadeScreenshotHotkey()) ||
            HotkeysCanConflict(normalized, GetAutoShadeAnalysisHotkey()))
        {
            ScreenFilterStatusText.Text = "屏幕调色: 三个自动滤镜热键的主键不能相同";
            return;
        }

        var previous = _screenFilterRequest.ToggleHotkey;
        if (!_screenFilterHotkeyService.Register(OwnerWindow, normalized))
        {
            var error = _screenFilterHotkeyService.LastError;
            _isCapturingScreenFilterHotkey = false;
            _screenFilterHotkeyService.Register(OwnerWindow, previous);
            InitializeAutoShadeAnalysisHotkey();
            UpdateScreenFilterHotkeyUi();
            ScreenFilterStatusText.Text = $"屏幕调色: 热键被占用或不可用（错误码 {error}）";
            return;
        }

        _isCapturingScreenFilterHotkey = false;
        _screenFilterRequest = CaptureScreenFilterRequest() with { ToggleHotkey = normalized };
        InitializeAutoShadeAnalysisHotkey();
        SaveScreenFilterRequest($"屏幕调色: 热键已设为 {GlobalHotkeyService.Format(normalized)}");
        UpdateScreenFilterHotkeyUi();
        RuntimeLogService.Info("屏幕调色", "滤镜全局热键已更新", $"热键: {GlobalHotkeyService.Format(normalized)}");
    }

    private void ScreenFilterHotkeyCaptureButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isCapturingScreenFilterHotkey) CancelScreenFilterHotkeyCapture();
    }

    private void ScreenFilterHotkeyClearButton_Click(object sender, RoutedEventArgs e) => ClearScreenFilterHotkey();

    private void ClearScreenFilterHotkey()
    {
        var wasCapturing = _isCapturingScreenFilterHotkey;
        _isCapturingScreenFilterHotkey = false;
        _screenFilterHotkeyService.Unregister();
        _screenFilterRequest = CaptureScreenFilterRequest() with { ToggleHotkey = GlobalHotkeyGesture.None };
        SaveScreenFilterRequest("屏幕调色: 热键已清除");
        if (wasCapturing) RestoreFilterHotkeysAfterCapture();
        UpdateScreenFilterHotkeyUi();
        RuntimeLogService.Info("屏幕调色", "滤镜全局热键已清除");
    }

    private void CancelScreenFilterHotkeyCapture()
    {
        _isCapturingScreenFilterHotkey = false;
        RestoreFilterHotkeysAfterCapture();
        UpdateScreenFilterHotkeyUi();
    }

    private void ScreenFilterHotkeyService_Pressed()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ScreenFilterHotkeyService_Pressed));
            return;
        }

        _screenFilterPreviewTimer.Stop();
        _screenFilterPreviewPending = false;
        if (GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade)
        {
            if (_autoShadeCaptureStarting || _autoShadeCapturePending)
            {
                DisableAutoShade("通过热键关闭自动滤镜", "已通过热键关闭并取消截图");
            }
            else if (_autoShadePaused)
            {
                EnableAutoShade();
            }
            else
            {
                DisableAutoShade("通过热键关闭自动滤镜", "已通过热键关闭");
            }

            UpdateScreenFilterHotkeyUi();
            return;
        }

        if (_screenGammaService.HasActiveAdjustments)
        {
            try
            {
                var result = _screenGammaService.ResetAll();
                if (result.FailedCount == 0) _activeScreenFilterKind = ActiveScreenFilterKind.None;
                ScreenFilterStatusText.Text = result.FailedCount == 0 ? "屏幕调色: 已通过热键关闭" : result.Message;
                RuntimeLogService.Info("屏幕调色", "已通过热键关闭滤镜", $"结果: {result.Message}");
            }
            catch (Exception exception)
            {
                ScreenFilterStatusText.Text = $"关闭滤镜失败: {exception.Message}";
                RuntimeLogService.Error("屏幕调色", "通过热键关闭滤镜失败", exception);
            }
        }
        else
        {
            ApplyScreenFilterNow(_screenFilterRequest, logChange: true);
        }

        UpdateScreenFilterHotkeyUi();
    }

    private void UpdateScreenFilterHotkeyUi()
    {
        if (ScreenFilterHotkeyCaptureButton is null || ScreenFilterHotkeyClearButton is null || ScreenFilterHotkeyStateText is null) return;
        if (!_isCapturingScreenFilterHotkey)
            ScreenFilterHotkeyCaptureButton.Content = GlobalHotkeyService.Format(_screenFilterRequest.ToggleHotkey);
        ScreenFilterHotkeyClearButton.IsEnabled = _screenFilterRequest.ToggleHotkey.IsConfigured;
        var autoShadeMode = GetSelectedScreenFilterMode() == ScreenFilterMode.AutoShade;
        var filterEnabled = autoShadeMode
            ? !_autoShadePaused && _activeScreenFilterKind == ActiveScreenFilterKind.AutoShade && _screenGammaService.HasActiveAdjustments
            : _screenGammaService.HasActiveAdjustments;
        ScreenFilterHotkeyStateText.Text = _autoShadeCaptureStarting || _autoShadeCapturePending
            ? "正在截图"
            : filterEnabled
                ? "滤镜已开启"
                : autoShadeMode && !_autoShadePaused
                    ? (_autoShadeProcessSuspended ? "等待游戏" : "等待截图分析")
                    : "滤镜已关闭";
        ScreenFilterHotkeyStateText.Foreground = filterEnabled
            ? FindResource("GreenBrush") as Brush ?? Brushes.LightGreen
            : FindResource("TextDimBrush") as Brush ?? Brushes.Gray;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static GlobalHotkeyModifiers GetPressedHotkeyModifiers()
    {
        var result = GlobalHotkeyModifiers.None;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= GlobalHotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= GlobalHotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= GlobalHotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= GlobalHotkeyModifiers.Windows;
        return result;
    }

    private void ScheduleScreenFilterPreview()
    {
        if (GetSelectedScreenFilterMode() != ScreenFilterMode.GammaPanel) return;
        _screenFilterPreviewPending = true;
        _screenFilterPreviewTimer.Stop();
        _screenFilterPreviewTimer.Start();
    }

    private void ApplyPendingScreenFilterPreview()
    {
        _screenFilterPreviewTimer.Stop();
        if (!_screenFilterPreviewPending) return;
        _screenFilterPreviewPending = false;
        if (GetSelectedScreenFilterMode() != ScreenFilterMode.GammaPanel) return;
        ApplyScreenFilterNow(_screenFilterRequest, logChange: false);
    }

    private void ApplyScreenFilterNow(ScreenFilterRequest request, bool logChange)
    {
        try
        {
            var result = _screenGammaService.Apply(request.ToPreset(), request.DisplayName);
            if (result.AppliedCount > 0) _activeScreenFilterKind = ActiveScreenFilterKind.GammaPanel;
            ScreenFilterStatusText.Text = result.Message;
            if (logChange)
            {
                RuntimeLogService.Info(
                    "屏幕调色",
                    "已应用 Gamma Panel 参数",
                    $"显示器: {(string.IsNullOrWhiteSpace(request.DisplayName) ? "全部屏幕" : request.DisplayName)}\n" +
                    $"预设: {request.PresetId}\n" +
                    $"红: G {request.Red.Gamma} / B {request.Red.Brightness} / C {request.Red.Contrast}\n" +
                    $"绿: G {request.Green.Gamma} / B {request.Green.Brightness} / C {request.Green.Contrast}\n" +
                    $"蓝: G {request.Blue.Gamma} / B {request.Blue.Brightness} / C {request.Blue.Contrast}\n" +
                    $"结果: {result.Message}");
            }
            UpdateScreenFilterHotkeyUi();
        }
        catch (Exception exception)
        {
            ScreenFilterStatusText.Text = $"屏幕调色失败: {exception.Message}";
            RuntimeLogService.Error("屏幕调色", "应用 Gamma Panel 参数失败", exception, $"显示器: {request.DisplayName}\n预设: {request.PresetId}");
            UpdateScreenFilterHotkeyUi();
        }
    }

    private void ScreenFilterResetButton_Click(object sender, RoutedEventArgs e)
    {
        var current = CaptureScreenFilterRequest();
        var resetRequest = current with
        {
            PresetId = "default",
            LinkChannels = true,
            Red = ScreenFilterChannel.Default,
            Green = ScreenFilterChannel.Default,
            Blue = ScreenFilterChannel.Default
        };
        _screenFilterPreviewTimer.Stop();
        _screenFilterPreviewPending = false;
        ApplyScreenFilterRequest(resetRequest, null);
        SaveScreenFilterRequest();
        try
        {
            var result = _screenGammaService.ResetAll();
            if (result.FailedCount == 0) _activeScreenFilterKind = ActiveScreenFilterKind.None;
            ScreenFilterStatusText.Text = result.Message;
            RuntimeLogService.Info("屏幕调色", "已恢复系统颜色", $"范围: 本次会话调整过的全部屏幕\n结果: {result.Message}");
            UpdateScreenFilterHotkeyUi();
        }
        catch (Exception exception)
        {
            ScreenFilterStatusText.Text = $"恢复默认失败: {exception.Message}";
            RuntimeLogService.Error("屏幕调色", "恢复系统颜色失败", exception, "范围: 本次会话调整过的全部屏幕");
            UpdateScreenFilterHotkeyUi();
        }
    }
}
