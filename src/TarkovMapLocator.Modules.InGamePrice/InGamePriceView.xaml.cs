using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.InGamePrice.Models;
using TarkovMapLocator.Modules.InGamePrice.Services;
using TarkovMapLocator.Modules.InGamePrice.Windows;

namespace TarkovMapLocator.Modules.InGamePrice;

public partial class InGamePriceView : UserControl, IFeatureViewLifecycle
{
    private const double InGamePriceDebugViewportMinZoom = 0.2;
    private const double InGamePriceDebugViewportMaxZoom = 6;
    private const double InGamePriceDebugViewportZoomStep = 1.16;
    private readonly IFeatureHost _host;
    private readonly CancellationTokenSource _disposeCts;
    private string _inGamePriceMode = "pvp";
    private InGamePriceRecognitionSettings _inGamePriceRecognitionSettings = InGamePriceRecognitionPreferencesService.Load();
    private InGamePriceRecognitionService? _inGamePriceRecognitionService;
    private InGamePriceMatch? _inGamePriceOverlayHoldMatch;
    private int _inGamePriceOverlayMissStreak;
    private InGamePriceLookupIndex? _inGamePriceLookupIndex;
    private InGamePriceOverlayWindow? _inGamePriceOverlayWindow;
    private CancellationTokenSource? _inGamePriceRecognitionCts;
    private Task? _inGamePriceRecognitionTask;
    private bool _inGamePriceRecognitionRunning;
    private bool _inGamePriceRecognitionStarting;
    private int _inGamePriceRecognitionSessionId;
    private string _inGamePriceRecognitionStatus = "未启动";
    private string? _inGamePriceRecognitionLastSignature;
    private InGamePriceRecognitionSample? _inGamePriceLatestSample;
    private InGamePriceMatch? _inGamePriceLatestMatch;
    private DateTimeOffset _inGamePriceRecognitionLastStatusUpdate;
    private bool _inGamePriceDebugEnabled;
    private bool _inGamePriceDebugCaptureRunning;
    private int _inGamePriceDebugCaptureVersion;
    private DateTimeOffset _inGamePriceLastDebugFrameAt;
    private CancellationTokenSource? _inGamePriceLiveDebugCaptureCts;
    private Task? _inGamePriceLiveDebugCaptureTask;
    private int _inGamePriceLiveDebugCaptureSessionId;
    private readonly object _inGamePriceLiveDebugFrameGate = new();
    private (int SessionId, InGamePriceDebugFrame Frame, BitmapSource Image)? _inGamePriceLiveDebugPendingFrame;
    private int _inGamePriceLiveDebugUpdateQueued;
    private InGamePriceDebugGeometry? _inGamePriceDebugGeometry;
    private InGamePriceDebugWindow? _inGamePriceDebugWindow;
    private int _inGamePriceDebugFrameWidth;
    private int _inGamePriceDebugFrameHeight;
    private double _inGamePriceDebugViewportZoom = 1;
    private double _inGamePriceDebugViewportPanX;
    private double _inGamePriceDebugViewportPanY;
    private bool _inGamePriceDebugViewportFitRequested = true;
    private bool _inGamePriceDebugViewportPanning;
    private MouseButton _inGamePriceDebugViewportPanButton;
    private Point _inGamePriceDebugViewportLastPanPoint;
    private bool _disposed;

    public InGamePriceView(IFeatureHost host)
    {
        _host = host;
        _disposeCts = CancellationTokenSource.CreateLinkedTokenSource(host.ShutdownToken);
        InitializeComponent();
        _inGamePriceRecognitionSettings = NormalizeMainOcrSettings(_inGamePriceRecognitionSettings);
        InGamePriceRecognitionPreferencesService.Save(_inGamePriceRecognitionSettings);
        _host.MarketDataChanged += Host_MarketDataChanged;
        UpdateInGamePriceRecognitionControls();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    public void OnActivated()
    {
        UpdateInGamePriceRecognitionControls();
        _ = EnsureMarketAvailableAsync();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _host.MarketDataChanged -= Host_MarketDataChanged;
        _disposeCts.Cancel();
        StopInGamePriceRecognition("主窗口关闭");
        StopInGamePriceLiveDebugCapture();
        CloseInGamePriceOverlayWindow();
        _inGamePriceDebugWindow?.Close();
        _inGamePriceDebugWindow = null;
        _disposeCts.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureMarketAvailableAsync()
    {
        try
        {
            if (GetMarketItems().Length == 0)
                await _host.EnsureMarketFreshAsync(_disposeCts.Token);
            if (!_disposed) RefreshInGamePriceLookupIndex();
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { }
        catch (Exception exception)
        {
            RuntimeLogService.Warning("游戏内查价", "读取行情数据失败", exception.Message);
        }
    }

    private FeatureMarketItem[] GetMarketItems() =>
        _host.GetMarketItems(_inGamePriceMode).Values.ToArray();

    private void Host_MarketDataChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshInGamePriceLookupIndex));
            return;
        }
        RefreshInGamePriceLookupIndex();
    }

    internal static InGamePriceRecognitionSettings NormalizeMainOcrSettings(InGamePriceRecognitionSettings settings)
    {
        var normalized = settings.Normalize();
        var captureRegion = normalized.CaptureRegion;
        var keepFixedCaptureRegion = captureRegion is { IsUsable: true } && normalized.IsCaptureRegionUserSelected;

        // Older settings did not record whether a fixed region was explicitly
        // selected.  Clear every pre-flag region once so automatic OCR never
        // inherits a stale full/half-screen capture. Subsequent selections are
        // persisted with IsCaptureRegionUserSelected = true.
        return normalized with
        {
            CaptureRegion = keepFixedCaptureRegion ? captureRegion : null,
            IsCaptureRegionUserSelected = keepFixedCaptureRegion
        };
    }

    private void InGamePriceAutoRepair_Click(object sender, RoutedEventArgs e)
    {
        var resetSettings = _inGamePriceRecognitionSettings with
        {
            CaptureRegion = null,
            IsCaptureRegionUserSelected = false
        };
        _inGamePriceRecognitionSettings = NormalizeMainOcrSettings(resetSettings);
        InGamePriceRecognitionPreferencesService.Save(_inGamePriceRecognitionSettings);
        _inGamePriceLastDebugFrameAt = DateTimeOffset.MinValue;
        QueueInGamePriceDebugFrameCapture();

        var status = _inGamePriceRecognitionRunning
            ? "自动定位已重置，已恢复鼠标附近扫描"
            : "自动模式已恢复默认：将扫描鼠标附近区域";
        SetInGamePriceRecognitionStatus(status, force: true);
        _host.ShowNotification(_inGamePriceRecognitionRunning
            ? "已重新扫描自动定位参数"
            : "已恢复自动 OCR 默认设置");
    }

    private async void InGamePriceRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (_inGamePriceRecognitionRunning)
        {
            StopInGamePriceRecognition("用户停止识别");
            return;
        }

        if (_inGamePriceRecognitionStarting) return;

        _inGamePriceRecognitionStarting = true;

        UpdateInGamePriceRecognitionControls();
        InGamePriceRecognitionService? service = null;

        try
        {
            var items = GetMarketItems();
            if (items.Length == 0)
            {
                SetInGamePriceRecognitionStatus("正在读取本地行情缓存…");
                try
                {
                    await _host.EnsureMarketFreshAsync(_disposeCts.Token);
                    items = GetMarketItems();
                }
                catch (OperationCanceledException) when (_disposed)
                {
                    return;
                }
                catch (Exception exception)
                {
                    RuntimeLogService.Error("游戏内查价", "读取行情缓存失败", exception);
                }
            }

            if (items.Length == 0)
            {
                SetInGamePriceRecognitionStatus("没有可用行情，请先刷新价格");
                _host.ShowNotification("游戏内查价需要先获得行情缓存");
                return;
            }

            var settings = NormalizeMainOcrSettings(_inGamePriceRecognitionSettings);
            _inGamePriceRecognitionSettings = settings;
            InGamePriceRecognitionPreferencesService.Save(settings);
            SetInGamePriceRecognitionStatus("正在初始化识图和中文 OCR…");
            service = await Task.Run(() => new InGamePriceRecognitionService(settings), _disposeCts.Token);
            if (_disposed)
            {
                service.Dispose();
                service = null;
                return;
            }

            _inGamePriceRecognitionSettings = settings;
            _inGamePriceLookupIndex = new InGamePriceLookupIndex(items);
            _inGamePriceOverlayWindow ??= new InGamePriceOverlayWindow();
            var activeService = service ?? throw new InvalidOperationException("OCR service initialization returned no service.");
            _inGamePriceRecognitionCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _inGamePriceRecognitionService = activeService;
            _inGamePriceRecognitionRunning = true;
            _inGamePriceRecognitionLastSignature = null;
            _inGamePriceLatestSample = null;
            _inGamePriceLatestMatch = null;
            ClearInGamePriceLiveDebugFrameQueue();
            var sessionId = ++_inGamePriceRecognitionSessionId;
            var cancellationToken = _inGamePriceRecognitionCts.Token;
            _inGamePriceRecognitionTask = Task.Run(() => RunInGamePriceRecognitionLoopAsync(activeService, settings, cancellationToken, sessionId));
            service = null; // Ownership transfers to the active recognition session.
            SetInGamePriceRecognitionStatus("自动定位中：请把鼠标停在游戏内物品名称附近…", force: true);
            RuntimeLogService.Info(
                "游戏内查价",
                "已启动屏幕识图查价",
                $"模式: {GetMarketModeDisplayName(_inGamePriceMode)}\n物品索引: {items.Length:N0}\n扫描间隔: {settings.ScanIntervalMilliseconds} ms\n截图范围: {DescribeInGamePriceCaptureRegion(settings)}\n定位方式: PP-OCR 自动检测");
        }
        catch (OperationCanceledException) when (_disposed)
        {
            SetInGamePriceRecognitionStatus("未启动");
        }
        catch (Exception exception)
        {
            service?.Dispose();
            SetInGamePriceRecognitionStatus("初始化失败：" + exception.Message);
            RuntimeLogService.Error("游戏内查价", "初始化识图查价失败", exception);
            _host.ShowNotification("游戏内查价启动失败，详情见运行日志");
        }
        finally
        {
            _inGamePriceRecognitionStarting = false;
            UpdateInGamePriceRecognitionControls();
        }
    }

    private void InGamePriceCaptureArea_Click(object sender, RoutedEventArgs e)
    {
        var restartRecognition = _inGamePriceRecognitionRunning;
        if (restartRecognition) StopInGamePriceRecognition("调整截图范围");
        var selector = new CaptureAreaSelectorWindow { Owner = OwnerWindow };
        if (selector.ShowDialog() != true || selector.SelectedRegion is not { } region)
        {
            if (restartRecognition)
                InGamePriceRecognition_Click(this, new RoutedEventArgs());
            return;
        }

        _inGamePriceRecognitionSettings = _inGamePriceRecognitionSettings with
        {
            CaptureRegion = region,
            IsCaptureRegionUserSelected = true
        };
        InGamePriceRecognitionPreferencesService.Save(_inGamePriceRecognitionSettings);
        SetInGamePriceRecognitionStatus(restartRecognition ? "已保存截图范围，正在重新启动识别" : "已保存截图范围", force: true);
        QueueInGamePriceDebugFrameCapture();
        RuntimeLogService.Info("游戏内查价", "已更新截图范围", $"X={region.X} · Y={region.Y} · 宽={region.Width} · 高={region.Height}");
        _host.ShowNotification($"截图范围已保存：{region.Width}×{region.Height}");
        if (restartRecognition)
            InGamePriceRecognition_Click(this, new RoutedEventArgs());
    }

    private void InGamePriceClearArea_Click(object sender, RoutedEventArgs e)
    {
        var restartRecognition = _inGamePriceRecognitionRunning;
        if (restartRecognition) StopInGamePriceRecognition("清除截图范围");
        _inGamePriceRecognitionSettings = _inGamePriceRecognitionSettings with
        {
            CaptureRegion = null,
            IsCaptureRegionUserSelected = false
        };
        InGamePriceRecognitionPreferencesService.Save(_inGamePriceRecognitionSettings);
        SetInGamePriceRecognitionStatus(restartRecognition ? "已恢复鼠标附近扫描，正在重新启动识别" : "已恢复为鼠标附近自动扫描", force: true);
        QueueInGamePriceDebugFrameCapture();
        RuntimeLogService.Info("游戏内查价", "已清除截图范围");
        if (restartRecognition)
            InGamePriceRecognition_Click(this, new RoutedEventArgs());
    }

    private void InGamePricePvp_Click(object sender, RoutedEventArgs e)
    {
        _inGamePriceMode = "pvp";
        RefreshInGamePriceLookupIndex();
        UpdateInGamePriceRecognitionControls();
    }

    private void InGamePricePve_Click(object sender, RoutedEventArgs e)
    {
        _inGamePriceMode = "pve";
        RefreshInGamePriceLookupIndex();
        UpdateInGamePriceRecognitionControls();
    }

    private void InGamePricePvpSeason_Click(object sender, RoutedEventArgs e)
    {
        _inGamePriceMode = "pvp-season";
        RefreshInGamePriceLookupIndex();
        UpdateInGamePriceRecognitionControls();
    }

    private void InGamePriceDebug_Click(object sender, RoutedEventArgs e)
    {
        SetInGamePriceDebugEnabled(!_inGamePriceDebugEnabled);
    }

    private void InGamePriceWindowedDebug_Click(object sender, RoutedEventArgs e)
    {
        SetInGamePriceDebugEnabled(true);
        OpenInGamePriceDebugWindow();
    }

    private void SetInGamePriceDebugEnabled(bool enabled)
    {
        if (_inGamePriceDebugEnabled == enabled)
        {
            if (enabled) StartInGamePriceLiveDebugCapture();
            UpdateInGamePriceRecognitionControls();
            return;
        }

        _inGamePriceDebugViewportPanning = false;
        if (InGamePriceDebugViewport.IsMouseCaptured)
            InGamePriceDebugViewport.ReleaseMouseCapture();
        _inGamePriceDebugEnabled = enabled;
        _inGamePriceDebugCaptureVersion++;
        _inGamePriceLastDebugFrameAt = DateTimeOffset.MinValue;
        if (!_inGamePriceDebugEnabled)
        {
            StopInGamePriceLiveDebugCapture();
            ClearInGamePriceDebugFrame();
            CloseInGamePriceDebugWindow();
            UpdateInGamePriceRecognitionControls();
            return;
        }

        StartInGamePriceLiveDebugCapture();
        UpdateInGamePriceRecognitionControls();
        if (!_inGamePriceRecognitionRunning)
            QueueInGamePriceDebugFrameCapture();
    }

    private void OpenInGamePriceDebugWindow()
    {
        if (_inGamePriceDebugWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        // This is deliberately an independent tool window: minimizing the main
        // workspace must not hide OCR diagnostics in progress.
        var debugWindow = new InGamePriceDebugWindow();
        debugWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_inGamePriceDebugWindow, debugWindow))
                _inGamePriceDebugWindow = null;
            UpdateInGamePriceRecognitionControls();
        };
        _inGamePriceDebugWindow = debugWindow;
        debugWindow.Show();
        RefreshInGamePriceDebugWindow();
        UpdateInGamePriceRecognitionControls();
    }

    private void CloseInGamePriceDebugWindow()
    {
        var debugWindow = _inGamePriceDebugWindow;
        _inGamePriceDebugWindow = null;
        debugWindow?.Close();
    }

    private void StartInGamePriceLiveDebugCapture()
    {
        if (!_inGamePriceDebugEnabled || _disposed || _inGamePriceLiveDebugCaptureCts is not null) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var sessionId = ++_inGamePriceLiveDebugCaptureSessionId;
        _inGamePriceLiveDebugCaptureCts = cancellation;
        _inGamePriceLiveDebugCaptureTask = Task.Run(
            () => RunInGamePriceLiveDebugCaptureLoopAsync(cancellation.Token, sessionId),
            CancellationToken.None);
    }

    private void StopInGamePriceLiveDebugCapture()
    {
        var cancellation = _inGamePriceLiveDebugCaptureCts;
        var task = _inGamePriceLiveDebugCaptureTask;
        _inGamePriceLiveDebugCaptureCts = null;
        _inGamePriceLiveDebugCaptureTask = null;
        _inGamePriceLiveDebugCaptureSessionId++;
        cancellation?.Cancel();
        ClearInGamePriceLiveDebugFrameQueue();

        if (task is null)
        {
            cancellation?.Dispose();
            return;
        }

        _ = task.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
    }

    private async Task RunInGamePriceLiveDebugCaptureLoopAsync(CancellationToken cancellationToken, int sessionId)
    {
        var lastFailureAt = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var captureCycle = Stopwatch.StartNew();
            var settings = _inGamePriceRecognitionSettings.Normalize();
            var intervalMilliseconds = Math.Max(45, settings.ScanIntervalMilliseconds / 2);
            try
            {
                var frame = InGamePriceDebugFrameCaptureService.Capture(
                    settings,
                    _inGamePriceDebugGeometry);
                QueueInGamePriceLiveDebugFrameUpdate(sessionId, frame, DecodeInGamePriceDebugFrame(frame));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (DateTimeOffset.UtcNow - lastFailureAt >= TimeSpan.FromSeconds(5))
                {
                    lastFailureAt = DateTimeOffset.UtcNow;
                    RuntimeLogService.Warning("游戏内查价", "实时调试截图失败，将继续重试", exception.Message);
                }
            }

            try
            {
                var remainingDelay = Math.Max(1, intervalMilliseconds - (int)captureCycle.ElapsedMilliseconds);
                await Task.Delay(remainingDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void QueueInGamePriceLiveDebugFrameUpdate(int sessionId, InGamePriceDebugFrame frame, BitmapSource image)
    {
        if (_disposed || !_inGamePriceDebugEnabled || sessionId != _inGamePriceLiveDebugCaptureSessionId) return;

        var shouldDispatch = false;
        lock (_inGamePriceLiveDebugFrameGate)
        {
            _inGamePriceLiveDebugPendingFrame = (sessionId, frame, image);
            if (Interlocked.CompareExchange(ref _inGamePriceLiveDebugUpdateQueued, 1, 0) == 0)
                shouldDispatch = true;
        }
        if (!shouldDispatch) return;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            (int SessionId, InGamePriceDebugFrame Frame, BitmapSource Image)? pending;
            lock (_inGamePriceLiveDebugFrameGate)
            {
                pending = _inGamePriceLiveDebugPendingFrame;
                _inGamePriceLiveDebugPendingFrame = null;
                Interlocked.Exchange(ref _inGamePriceLiveDebugUpdateQueued, 0);
            }

            if (pending is not { } update ||
                _disposed ||
                !_inGamePriceDebugEnabled ||
                update.SessionId != _inGamePriceLiveDebugCaptureSessionId)
                return;

            var sample = _inGamePriceLatestSample ?? new InGamePriceRecognitionSample(false, string.Empty, 0, 0, 0);
            ApplyInGamePriceDebugFrame(update.Frame, sample, _inGamePriceLatestMatch, update.Image);
        }));
    }

    private void ClearInGamePriceLiveDebugFrameQueue()
    {
        lock (_inGamePriceLiveDebugFrameGate)
            _inGamePriceLiveDebugPendingFrame = null;
    }

    private void QueueInGamePriceDebugFrameCapture()
    {
        // While recognition is active, the lightweight live capture loop owns
        // the debug viewport. Starting another OCR service here would add a
        // competing full recognition pass for every calibration gesture.
        if (!_inGamePriceDebugEnabled || _disposed || _inGamePriceRecognitionRunning) return;
        _inGamePriceDebugCaptureVersion++;
        _ = CaptureInGamePriceDebugFrameAsync();
    }

    private async Task CaptureInGamePriceDebugFrameAsync()
    {
        if (!_inGamePriceDebugEnabled || _inGamePriceDebugCaptureRunning) return;

        var captureVersion = _inGamePriceDebugCaptureVersion;
        _inGamePriceDebugCaptureRunning = true;
        InGamePriceDebugStatusText.Text = "正在抓取并标注调试画面…";
        RefreshInGamePriceDebugWindow();
        try
        {
            var settings = _inGamePriceRecognitionSettings.Normalize();
            var result = await Task.Run(() =>
            {
                using var service = new InGamePriceRecognitionService(settings);
                var sample = service.Recognize(CancellationToken.None, includeDebugFrame: true);
                var image = sample.DebugFrame is { } frame ? DecodeInGamePriceDebugFrame(frame) : null;
                return (Sample: sample, Image: image);
            }, _disposeCts.Token);

            if (_disposed || !_inGamePriceDebugEnabled || captureVersion != _inGamePriceDebugCaptureVersion) return;
            if (result.Sample.DebugFrame is { } debugFrame)
                ApplyInGamePriceDebugFrame(debugFrame, result.Sample, null, result.Image);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Window shutdown cancels one-off debug capture as well.
        }
        catch (Exception exception)
        {
            InGamePriceDebugStatusText.Text = "调试截图失败：" + exception.Message;
            RefreshInGamePriceDebugWindow();
            RuntimeLogService.Error("游戏内查价", "抓取 OCR 调试画面失败", exception);
        }
        finally
        {
            _inGamePriceDebugCaptureRunning = false;
            if (_inGamePriceDebugEnabled && !_disposed && captureVersion != _inGamePriceDebugCaptureVersion)
                _ = CaptureInGamePriceDebugFrameAsync();
        }
    }

    private void ApplyInGamePriceDebugFrame(
        InGamePriceDebugFrame debugFrame,
        InGamePriceRecognitionSample sample,
        InGamePriceMatch? match,
        BitmapSource? decodedImage = null)
    {
        try
        {
            var image = decodedImage ?? DecodeInGamePriceDebugFrame(debugFrame);
            var dimensionsChanged = _inGamePriceDebugFrameWidth != debugFrame.Width ||
                                    _inGamePriceDebugFrameHeight != debugFrame.Height;
            InGamePriceDebugImage.Source = image;
            _inGamePriceDebugFrameWidth = debugFrame.Width;
            _inGamePriceDebugFrameHeight = debugFrame.Height;
            InGamePriceDebugImage.Width = debugFrame.Width;
            InGamePriceDebugImage.Height = debugFrame.Height;
            InGamePriceDebugTransformHost.Width = debugFrame.Width;
            InGamePriceDebugTransformHost.Height = debugFrame.Height;
            InGamePriceDebugOverlayCanvas.Width = debugFrame.Width;
            InGamePriceDebugOverlayCanvas.Height = debugFrame.Height;
            _inGamePriceDebugGeometry = debugFrame.Geometry;
            InGamePriceDebugEmptyText.Visibility = Visibility.Collapsed;
            var ocrText = string.IsNullOrWhiteSpace(sample.OcrText) ? "未读取到文字" : sample.OcrText;
            var result = match is null
                ? sample.TemplateFound ? "未匹配行情" : "未定位到文字"
                : "匹配：" + InGamePriceOverlayFormatter.GetDisplayName(match.Item);
            var issue = string.IsNullOrWhiteSpace(sample.Issue) ? string.Empty : " · " + sample.Issue;
            InGamePriceDebugStatusText.Text = $"识别置信度 {sample.TemplateScore:P1} · OCR：{ocrText} · {result}{issue}";
            if (dimensionsChanged) _inGamePriceDebugViewportFitRequested = true;
            if (_inGamePriceDebugViewportFitRequested) FitInGamePriceDebugViewport();
            RefreshInGamePriceDebugWindow();
        }
        catch (Exception exception)
        {
            InGamePriceDebugStatusText.Text = "调试画面加载失败：" + exception.Message;
            RefreshInGamePriceDebugWindow();
            RuntimeLogService.Warning("游戏内查价", "加载 OCR 调试画面失败", exception.Message);
        }
    }

    private void ClearInGamePriceDebugFrame()
    {
        if (InGamePriceDebugImage is null) return;
        InGamePriceDebugImage.Source = null;
        _inGamePriceDebugFrameWidth = 0;
        _inGamePriceDebugFrameHeight = 0;
        InGamePriceDebugImage.Width = 0;
        InGamePriceDebugImage.Height = 0;
        InGamePriceDebugTransformHost.Width = 0;
        InGamePriceDebugTransformHost.Height = 0;
        InGamePriceDebugOverlayCanvas.Width = 0;
        InGamePriceDebugOverlayCanvas.Height = 0;
        _inGamePriceDebugViewportZoom = 1;
        _inGamePriceDebugViewportPanX = 0;
        _inGamePriceDebugViewportPanY = 0;
        _inGamePriceDebugViewportFitRequested = true;
        _inGamePriceDebugViewportPanning = false;
        ApplyInGamePriceDebugViewportTransform();
        _inGamePriceDebugGeometry = null;
        InGamePriceDebugViewport.Cursor = Cursors.Arrow;
        InGamePriceDebugEmptyText.Visibility = Visibility.Visible;
        InGamePriceDebugStatusText.Text = "调试框已关闭。";
        RefreshInGamePriceDebugWindow();
    }

    private static BitmapSource DecodeInGamePriceDebugFrame(InGamePriceDebugFrame debugFrame)
    {
        using var stream = new MemoryStream(debugFrame.PngBytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private bool HasInGamePriceDebugViewportFrame =>
        InGamePriceDebugImage.Source is not null &&
        _inGamePriceDebugFrameWidth > 0 &&
        _inGamePriceDebugFrameHeight > 0;

    private void InGamePriceDebugViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_inGamePriceDebugViewportFitRequested) FitInGamePriceDebugViewport();
    }

    private void FitInGamePriceDebugViewport()
    {
        if (!HasInGamePriceDebugViewportFrame ||
            InGamePriceDebugViewport.ActualWidth <= 0 ||
            InGamePriceDebugViewport.ActualHeight <= 0)
        {
            _inGamePriceDebugViewportFitRequested = true;
            return;
        }

        _inGamePriceDebugViewportZoom = Math.Clamp(
            Math.Min(
                InGamePriceDebugViewport.ActualWidth / _inGamePriceDebugFrameWidth,
                InGamePriceDebugViewport.ActualHeight / _inGamePriceDebugFrameHeight),
            InGamePriceDebugViewportMinZoom,
            InGamePriceDebugViewportMaxZoom);
        _inGamePriceDebugViewportPanX =
            (InGamePriceDebugViewport.ActualWidth - _inGamePriceDebugFrameWidth * _inGamePriceDebugViewportZoom) / 2;
        _inGamePriceDebugViewportPanY =
            (InGamePriceDebugViewport.ActualHeight - _inGamePriceDebugFrameHeight * _inGamePriceDebugViewportZoom) / 2;
        _inGamePriceDebugViewportFitRequested = false;
        ApplyInGamePriceDebugViewportTransform();
    }

    private Point ToInGamePriceDebugViewportImagePoint(Point viewportPoint) => new(
        (viewportPoint.X - _inGamePriceDebugViewportPanX) / _inGamePriceDebugViewportZoom,
        (viewportPoint.Y - _inGamePriceDebugViewportPanY) / _inGamePriceDebugViewportZoom);

    private void ApplyInGamePriceDebugViewportTransform()
    {
        InGamePriceDebugScaleTransform.ScaleX = _inGamePriceDebugViewportZoom;
        InGamePriceDebugScaleTransform.ScaleY = _inGamePriceDebugViewportZoom;
        InGamePriceDebugTranslateTransform.X = _inGamePriceDebugViewportPanX;
        InGamePriceDebugTranslateTransform.Y = _inGamePriceDebugViewportPanY;
    }

    private void RefreshInGamePriceDebugWindow()
    {
        if (_inGamePriceDebugWindow is not { IsVisible: true } debugWindow) return;
        debugWindow.UpdateFrame(
            InGamePriceDebugImage.Source,
            _inGamePriceDebugFrameWidth,
            _inGamePriceDebugFrameHeight,
            InGamePriceDebugStatusText.Text,
            InGamePriceDebugShortcutText.Text);
    }

    private void InGamePriceDebugViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasInGamePriceDebugViewportFrame) return;

        var viewportPoint = e.GetPosition(InGamePriceDebugViewport);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        BeginInGamePriceDebugViewportPanning(viewportPoint, e.ChangedButton);
        e.Handled = true;
    }

    private void InGamePriceDebugViewport_MouseMove(object sender, MouseEventArgs e)
    {
        var viewportPoint = e.GetPosition(InGamePriceDebugViewport);
        if (_inGamePriceDebugViewportPanning)
        {
            _inGamePriceDebugViewportPanX += viewportPoint.X - _inGamePriceDebugViewportLastPanPoint.X;
            _inGamePriceDebugViewportPanY += viewportPoint.Y - _inGamePriceDebugViewportLastPanPoint.Y;
            _inGamePriceDebugViewportLastPanPoint = viewportPoint;
            ApplyInGamePriceDebugViewportTransform();
            e.Handled = true;
            return;
        }
    }

    private void InGamePriceDebugViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_inGamePriceDebugViewportPanning && e.ChangedButton == _inGamePriceDebugViewportPanButton)
        {
            EndInGamePriceDebugViewportPanning();
            e.Handled = true;
            return;
        }
    }

    private void InGamePriceDebugViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasInGamePriceDebugViewportFrame || e.Delta == 0) return;

        var steps = Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120);
        var nextZoom = Math.Clamp(
            _inGamePriceDebugViewportZoom * Math.Pow(InGamePriceDebugViewportZoomStep, steps),
            InGamePriceDebugViewportMinZoom,
            InGamePriceDebugViewportMaxZoom);
        if (Math.Abs(nextZoom - _inGamePriceDebugViewportZoom) < 0.0001) return;

        var viewportPoint = e.GetPosition(InGamePriceDebugViewport);
        var imagePoint = ToInGamePriceDebugViewportImagePoint(viewportPoint);
        _inGamePriceDebugViewportZoom = nextZoom;
        _inGamePriceDebugViewportPanX = viewportPoint.X - imagePoint.X * nextZoom;
        _inGamePriceDebugViewportPanY = viewportPoint.Y - imagePoint.Y * nextZoom;
        _inGamePriceDebugViewportFitRequested = false;
        ApplyInGamePriceDebugViewportTransform();
        e.Handled = true;
    }

    private void InGamePriceDebugViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_inGamePriceDebugViewportPanning)
        {
            _inGamePriceDebugViewportPanning = false;
            InGamePriceDebugViewport.Cursor = Cursors.Arrow;
        }
    }

    private void BeginInGamePriceDebugViewportPanning(Point viewportPoint, MouseButton button)
    {
        _inGamePriceDebugViewportPanning = true;
        _inGamePriceDebugViewportPanButton = button;
        _inGamePriceDebugViewportLastPanPoint = viewportPoint;
        _inGamePriceDebugViewportFitRequested = false;
        InGamePriceDebugViewport.Cursor = Cursors.SizeAll;
        InGamePriceDebugViewport.CaptureMouse();
    }

    private void EndInGamePriceDebugViewportPanning()
    {
        _inGamePriceDebugViewportPanning = false;
        InGamePriceDebugViewport.Cursor = Cursors.Arrow;
        if (InGamePriceDebugViewport.IsMouseCaptured) InGamePriceDebugViewport.ReleaseMouseCapture();
    }


    private async Task RunInGamePriceRecognitionLoopAsync(
        InGamePriceRecognitionService service,
        InGamePriceRecognitionSettings settings,
        CancellationToken cancellationToken,
        int sessionId)
    {
        var matchGate = new InGamePriceMatchGate();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                InGamePriceRecognitionSample sample;
                try
                {
                    // The live debug loop captures its own lightweight screen
                    // frames at half the recognition interval. Keep this OCR
                    // pass focused on matching so debug mode does not halve
                    // the responsiveness of normal price recognition.
                    sample = service.Recognize(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    RuntimeLogService.Warning("游戏内查价", "本轮屏幕识别失败，将继续重试", exception.Message);
                    QueueInGamePriceRecognitionUpdate(sessionId, null, null, "识别异常，正在重试…");
                    await Task.Delay(Math.Max(settings.ScanIntervalMilliseconds, 700), cancellationToken);
                    continue;
                }

                var index = _inGamePriceLookupIndex;
                InGamePriceMatch? candidate = null;
                if (index is not null)
                {
                    if (sample.Candidates is { Count: > 0 } lines)
                    {
                        // The detection pipeline returns several text lines per
                        // frame; the first one that resolves to a market item
                        // becomes the anchor the overlay attaches to.
                        foreach (var line in lines)
                        {
                            candidate = index.FindBest(line.Text, settings.MatchSimilarity);
                            if (candidate is null) continue;
                            sample = sample with
                            {
                                OcrText = line.Text,
                                TemplateScore = line.Confidence,
                                TemplateX = line.AnchorX,
                                TemplateY = line.AnchorY,
                                AnchorScale = line.AnchorScale
                            };
                            break;
                        }
                    }
                    else if (sample.TemplateFound && !string.IsNullOrWhiteSpace(sample.OcrText))
                    {
                        candidate = index.FindBest(sample.OcrText, settings.MatchSimilarity);
                    }
                }
                var match = matchGate.Accept(candidate);

                var status = match is not null
                    ? $"识别中：{InGamePriceOverlayFormatter.GetDisplayName(match.Item)}"
                    : candidate is not null
                        ? $"识别确认中：{InGamePriceOverlayFormatter.GetDisplayName(candidate.Item)}"
                    : !sample.TemplateFound
                        ? string.IsNullOrWhiteSpace(sample.Issue)
                            ? "等待物品详情窗口：请在游戏中打开物品检视界面…"
                            : sample.Issue
                    : sample.Candidates is { Count: > 0 } unmatched
                        ? $"未匹配行情：{unmatched[0].Text}（共 {unmatched.Count} 行）"
                    : string.IsNullOrWhiteSpace(sample.OcrText)
                        ? string.IsNullOrWhiteSpace(sample.Issue)
                            ? "已定位物品提示，等待文字识别…"
                            : sample.Issue
                        : $"未匹配：{sample.OcrText}";
                QueueInGamePriceRecognitionUpdate(sessionId, sample, match, status);
                await Task.Delay(settings.ScanIntervalMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the user stops recognition or closes the application.
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("游戏内查价", "识图循环意外退出", exception);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_inGamePriceRecognitionRunning || sessionId != _inGamePriceRecognitionSessionId) return;
                StopInGamePriceRecognition("识别循环异常退出");
                SetInGamePriceRecognitionStatus("识别循环已停止，详见运行日志", force: true);
            }));
        }
    }

    private void QueueInGamePriceRecognitionUpdate(
        int sessionId,
        InGamePriceRecognitionSample? sample,
        InGamePriceMatch? match,
        string status)
    {
        var debugImage = sample?.DebugFrame is { } pendingFrame
            ? DecodeInGamePriceDebugFrame(pendingFrame)
            : null;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed || !_inGamePriceRecognitionRunning || sessionId != _inGamePriceRecognitionSessionId) return;
            _inGamePriceLatestSample = sample;
            _inGamePriceLatestMatch = match;
            if (sample?.Geometry is not null)
                _inGamePriceDebugGeometry = sample.Geometry;
            if (match is not null && sample is not null)
            {
                _inGamePriceOverlayHoldMatch = match;
                _inGamePriceOverlayMissStreak = 0;
                _inGamePriceOverlayWindow?.ShowMatch(_inGamePriceRecognitionSettings, match, sample.TemplateX, sample.TemplateY, sample.AnchorScale);
                var signature = $"{match.Item.Id}|{sample.OcrText}";
                if (!string.Equals(signature, _inGamePriceRecognitionLastSignature, StringComparison.Ordinal))
                {
                    _inGamePriceRecognitionLastSignature = signature;
                    RuntimeLogService.Info(
                        "游戏内查价",
                        "识别到物品并显示悬浮价格",
                        $"OCR: {sample.OcrText}\n匹配: {InGamePriceOverlayFormatter.GetDisplayName(match.Item)}\n相似度: {match.Similarity:P0}\n识别置信度: {sample.TemplateScore:P0}");
                }
            }
            else if (sample is { TemplateFound: true } && _inGamePriceOverlayHoldMatch is not null && _inGamePriceOverlayMissStreak < 2)
            {
                // A single failed OCR frame used to blink the price tag off and
                // straight back on. Hold the last match through brief misses —
                // but only while the weight line still proves the window is
                // open. Once it disappears (window closed), hide immediately.
                _inGamePriceOverlayMissStreak++;
            }
            else
            {
                _inGamePriceOverlayHoldMatch = null;
                _inGamePriceOverlayMissStreak = 0;
                _inGamePriceOverlayWindow?.HidePrice();
            }

            if (_inGamePriceDebugEnabled && sample?.DebugFrame is { } debugFrame)
                ApplyInGamePriceDebugFrame(debugFrame, sample, match, debugImage);

            SetInGamePriceRecognitionStatus(status);
        }));
    }

    private void StopInGamePriceRecognition(string reason)
    {
        var wasRunning = _inGamePriceRecognitionRunning;
        var cancellation = _inGamePriceRecognitionCts;
        var task = _inGamePriceRecognitionTask;
        var service = _inGamePriceRecognitionService;
        var hadSession = task is not null || service is not null;
        _inGamePriceRecognitionRunning = false;
        _inGamePriceRecognitionCts = null;
        _inGamePriceRecognitionTask = null;
        _inGamePriceRecognitionService = null;
        _inGamePriceLookupIndex = null;
        _inGamePriceRecognitionLastSignature = null;
        _inGamePriceLatestSample = null;
        _inGamePriceLatestMatch = null;
        _inGamePriceOverlayHoldMatch = null;
        _inGamePriceOverlayMissStreak = 0;
        _inGamePriceRecognitionSessionId++;
        cancellation?.Cancel();
        ClearInGamePriceLiveDebugFrameQueue();
        CloseInGamePriceOverlayWindow();

        SetInGamePriceRecognitionStatus("未启动", force: true);

        if (task is null)
        {
            service?.Dispose();
            cancellation?.Dispose();
            if (hadSession) QueueReleasedInGamePriceMemoryCollection();
        }
        else
        {
            _ = task.ContinueWith(_ =>
            {
                try { service?.Dispose(); }
                finally
                {
                    cancellation?.Dispose();
                    if (hadSession) QueueReleasedInGamePriceMemoryCollection();
                }
            }, TaskScheduler.Default);
        }

        if (wasRunning)
            RuntimeLogService.Info("游戏内查价", "已停止屏幕识图查价", reason);
    }

    private static void QueueReleasedInGamePriceMemoryCollection()
    {
        // ONNX and OpenCV own most OCR memory natively and are released by
        // Dispose. A generation-2 collection then drops the large managed
        // tensors and PNG buffers that became unreachable with the session.
        // Run away from the UI thread because an explicit feature shutdown is
        // a better boundary for this pause than a later interaction frame.
        _ = Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        });
    }
    private void CloseInGamePriceOverlayWindow()
    {
        var overlay = _inGamePriceOverlayWindow;
        _inGamePriceOverlayWindow = null;
        if (overlay is null) return;

        // Hiding alone left a short race window where the last queued match
        // could retain a native transparent surface. A stop is a hard session
        // boundary, so destroy the overlay and recreate it on the next start.
        try
        {
            overlay.HidePrice();
            overlay.Close();
        }
        catch (InvalidOperationException)
        {
            // The window may already be closing as the main WPF window exits.
        }
    }

    private void RefreshInGamePriceLookupIndex()
    {
        if (!_inGamePriceRecognitionRunning) return;
        var items = GetMarketItems();
        if (items.Length == 0)
        {
            _inGamePriceLookupIndex = null;
            _inGamePriceLatestMatch = null;
            _inGamePriceOverlayHoldMatch = null;
            _inGamePriceOverlayMissStreak = 0;
            _inGamePriceOverlayWindow?.HidePrice();
            SetInGamePriceRecognitionStatus("当前模式没有可用行情");
            return;
        }

        _inGamePriceLookupIndex = new InGamePriceLookupIndex(items);
        SetInGamePriceRecognitionStatus($"识别中，已切换到 {GetMarketModeDisplayName(_inGamePriceMode)} 行情");
        RuntimeLogService.Info("游戏内查价", "已更新物品识别索引", $"模式: {GetMarketModeDisplayName(_inGamePriceMode)}\n物品: {items.Length:N0}");
    }

    private void SetInGamePriceRecognitionStatus(string status, bool force = false)
    {
        if (!force && string.Equals(status, _inGamePriceRecognitionStatus, StringComparison.Ordinal) &&
            DateTimeOffset.Now - _inGamePriceRecognitionLastStatusUpdate < TimeSpan.FromSeconds(2)) return;
        _inGamePriceRecognitionStatus = status;
        _inGamePriceRecognitionLastStatusUpdate = DateTimeOffset.Now;
        UpdateInGamePriceRecognitionControls();
    }

    private void UpdateInGamePriceRecognitionControls()
    {
        if (InGamePricePageRecognitionButton is null) return;
        var settings = _inGamePriceRecognitionSettings.Normalize();
        var recognitionText = _inGamePriceRecognitionStarting
            ? "正在启动…"
            : _inGamePriceRecognitionRunning ? "停止识别" : "启动自动识别";
        var regionDescriptor = settings.CaptureRegion is { IsUsable: true } fixedRegion
            ? $"固定区域 {fixedRegion.Width}×{fixedRegion.Height}"
            : $"鼠标附近 {settings.AutoSearchWidth}×{settings.AutoSearchHeight}";
        var status = $"{_inGamePriceRecognitionStatus} · {GetMarketModeDisplayName(_inGamePriceMode)} · 自动定位（{regionDescriptor} / PP-OCR）";
        InGamePricePageRecognitionButton.Content = recognitionText;
        InGamePricePageRecognitionButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePricePageStatusText.Text = status;
        InGamePriceAutoRepairButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePricePageCaptureAreaButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePricePageClearAreaButton.IsEnabled = !_inGamePriceRecognitionStarting && settings.CaptureRegion is { IsUsable: true };
        InGamePriceDebugButton.Content = _inGamePriceDebugEnabled ? "关闭定位画面" : "查看定位画面";
        InGamePriceDebugButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePriceWindowedDebugButton.Content = _inGamePriceDebugWindow is { IsVisible: true } ? "定位画面已打开" : "窗口化定位画面";
        InGamePriceWindowedDebugButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePriceWindowedDebugButton.ToolTip = "打开可调整大小的独立定位画面，便于截图反馈";
        InGamePricePagePvpButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePricePagePveButton.IsEnabled = !_inGamePriceRecognitionStarting;
        InGamePricePagePvpSeasonButton.IsEnabled = !_inGamePriceRecognitionStarting;
        SetToolButton(InGamePricePagePvpButton, _inGamePriceMode == "pvp");
        SetToolButton(InGamePricePagePveButton, _inGamePriceMode == "pve");
        SetToolButton(InGamePricePagePvpSeasonButton, _inGamePriceMode == "pvp-season");
        InGamePriceDebugShortcutText.Text =
            "PP-OCR 自动反馈：红框=物品标题，绿框=重量 kg，蓝框=悬浮价签；可滚轮缩放、空白处拖动平移。";
        RefreshInGamePriceDebugWindow();

        if (!_inGamePriceDebugEnabled && string.IsNullOrWhiteSpace(InGamePriceDebugStatusText.Text))
            InGamePriceDebugStatusText.Text = "调试框关闭：PP-OCR 会标出物品标题、重量 kg 和悬浮价签，仅用于确认识别结果。";
    }

    private static string DescribeInGamePriceCaptureRegion(InGamePriceRecognitionSettings settings) =>
        settings.CaptureRegion is { IsUsable: true } region
            ? $"范围 {region.Width}×{region.Height}"
            : $"鼠标附近 {settings.AutoSearchWidth}×{settings.AutoSearchHeight}";

    private static string GetMarketModeDisplayName(string mode) =>
        string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? "PVE" :
        string.Equals(mode, "pvp-season", StringComparison.OrdinalIgnoreCase) ? "赛季服" :
        "PVP";
    private void SetToolButton(Button button, bool selected)
    {
        button.Background = selected ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent;
        button.BorderBrush = selected ? (Brush)FindResource("LineBrightBrush") : (Brush)FindResource("LineBrush");
        button.Foreground = selected ? (Brush)FindResource("AmberBrush") : (Brush)FindResource("TextDimBrush");
    }
}
