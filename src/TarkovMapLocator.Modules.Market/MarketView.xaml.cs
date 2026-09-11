using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Market;

public partial class MarketView : UserControl
{
    private const int BatchSize = 80;
    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    private readonly IFeatureHost _host;
    private readonly ObservableCollection<MarketItemRow> _visibleRows = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private IReadOnlyList<FeatureMarketItem> _filteredItems = [];
    private FeatureMarketSnapshot _snapshot = new([], null, "", "正在加载", true, false);
    private string _mode;
    private string _statusBase = "正在加载";
    private string? _renderKey;
    private string? _expandedItemId;
    private MarketItemRow? _expandedRow;
    private bool _appendingRows;
    private bool _refreshing;
    private bool _subscribed;
    private int _batchVersion;

    public MarketView(IFeatureHost host)
    {
        _host = host;
        _mode = NormalizeMode(host.CurrentMarketMode);
        InitializeComponent();
        AsyncItemIcon.SetFeatureHost(this, host);
        MarketResultsList.ItemsSource = _visibleRows;
        _searchTimer.Tick += MarketSearchTimer_Tick;
        Loaded += MarketView_Loaded;
        Unloaded += MarketView_Unloaded;
        IsVisibleChanged += MarketView_IsVisibleChanged;
        UpdateMarketResults();
    }

    private async void MarketView_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        await EnsureMarketAsync();
    }

    private void MarketView_Unloaded(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        Unsubscribe();
        _batchVersion++;
    }

    private async void MarketView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && IsLoaded)
        {
            Subscribe();
            await EnsureMarketAsync();
        }
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _host.MarketDataChanged += Host_MarketDataChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _host.MarketDataChanged -= Host_MarketDataChanged;
        _subscribed = false;
    }

    private void Host_MarketDataChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateMarketResults));
            return;
        }
        UpdateMarketResults();
    }

    private async Task EnsureMarketAsync()
    {
        if (_refreshing) return;
        SetRefreshing(true);
        MarketStatusText.Text = _snapshot.IsAvailable ? "正在检查行情更新，当前继续显示本地缓存…" : "正在加载…";
        try
        {
            await _host.EnsureMarketFreshAsync(_host.ShutdownToken);
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _host.WriteLog(FeatureLogLevel.Error, "市场", "加载行情失败", exception.ToString());
        }
        finally
        {
            SetRefreshing(false);
            UpdateMarketResults();
        }
    }

    private void MarketSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void MarketSearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        UpdateMarketResults();
    }

    private void MarketPvp_Click(object sender, RoutedEventArgs e) => SetMode("pvp");
    private void MarketPve_Click(object sender, RoutedEventArgs e) => SetMode("pve");
    private void MarketPvpSeason_Click(object sender, RoutedEventArgs e) => SetMode("pvp-season");

    private void SetMode(string mode)
    {
        _mode = mode;
        _host.SetMarketMode(mode);
        _renderKey = null;
        UpdateMarketResults();
    }

    private async void MarketRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        SetRefreshing(true);
        MarketStatusText.Text = "正在读取 PVP、PVE 与赛季服行情…";
        _host.WriteLog(FeatureLogLevel.Info, "市场", "用户手动刷新行情", $"当前模式: {ModeDisplayName(_mode)}");
        try
        {
            await _host.RefreshMarketAsync(_host.ShutdownToken);
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _host.WriteLog(FeatureLogLevel.Error, "市场", "手动刷新行情失败", exception.ToString());
            _host.ShowNotification("价格刷新失败，已保留上次缓存");
        }
        finally
        {
            SetRefreshing(false);
            UpdateMarketResults();
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        _refreshing = refreshing;
        MarketRefreshButton.IsEnabled = !refreshing;
    }

    private void UpdateMarketResults()
    {
        if (MarketResultsList is null) return;
        SetToolButton(MarketPvpButton, _mode == "pvp");
        SetToolButton(MarketPveButton, _mode == "pve");
        SetToolButton(MarketPvpSeasonButton, _mode == "pvp-season");

        _snapshot = _host.GetMarketSnapshot(_mode);
        var items = _snapshot.Items;
        var term = MarketSearchBox?.Text.Trim() ?? "";
        var visible = string.IsNullOrWhiteSpace(term)
            ? items.Where(item => item.FleaPrice is not null).OrderByDescending(item => item.FleaPrice ?? item.Avg24hPrice ?? 0).ToArray()
            : items.Where(item => MarketMatches(item, term)).OrderBy(item => MarketMatchScore(item, term)).ThenByDescending(item => item.FleaPrice ?? item.Avg24hPrice ?? 0).ToArray();

        _statusBase = _snapshot.IsAvailable
            ? $"{ModeDisplayName(_mode)} · {(_snapshot.IsStale ? "缓存超过 12 小时" : "缓存有效")} · {items.Count:N0} 件物品 · 跳蚤报价 {items.Count(item => item.FleaPrice is not null):N0} 条 · 更新 {_snapshot.UpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "未知"}" + (_snapshot.ErrorMessage is { Length: > 0 } error ? $" · {error}" : "")
            : _snapshot.ErrorMessage ?? "暂无行情数据";
        if (_refreshing) _statusBase += _snapshot.IsAvailable ? " · 正在更新" : " · 正在获取行情";

        var renderKey = string.Join("\u001f", _mode, term, _snapshot.UpdatedAt?.Ticks ?? 0, items.Count, _snapshot.Source, _snapshot.ErrorMessage ?? "");
        if (string.Equals(_renderKey, renderKey, StringComparison.Ordinal))
        {
            UpdateMarketStatusText();
            return;
        }

        _renderKey = renderKey;
        _filteredItems = visible;
        _batchVersion++;
        _appendingRows = false;
        _expandedRow = null;
        _visibleRows.Clear();

        if (visible.Length == 0)
        {
            MarketEmptyText.Text = string.IsNullOrWhiteSpace(term) ? "没有可显示的价格。点击“刷新价格”获取行情。" : "没有匹配的物品。";
            MarketEmptyText.Visibility = Visibility.Visible;
            UpdateMarketStatusText();
            return;
        }

        if (_expandedItemId is not null && !visible.Any(item => string.Equals(item.Id, _expandedItemId, StringComparison.Ordinal)))
            _expandedItemId = null;
        MarketEmptyText.Visibility = Visibility.Collapsed;
        LoadNextMarketBatch();
    }

    private void MarketResultsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsVisible || _appendingRows || e.ViewportHeight <= 0) return;
        var remaining = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset;
        if (remaining <= Math.Max(8, e.ViewportHeight * .75)) LoadNextMarketBatch();
    }

    private void LoadNextMarketBatch()
    {
        if (_appendingRows || _visibleRows.Count >= _filteredItems.Count) return;
        _appendingRows = true;
        _ = LoadNextMarketBatchAsync(_batchVersion, _visibleRows.Count);
    }

    private async Task LoadNextMarketBatchAsync(int version, int startIndex)
    {
        try
        {
            var endIndex = Math.Min(startIndex + BatchSize, _filteredItems.Count);
            for (var index = startIndex; index < endIndex; index++)
            {
                if (version != _batchVersion || _host.ShutdownToken.IsCancellationRequested) return;
                var item = _filteredItems[index];
                _visibleRows.Add(new MarketItemRow(item, string.Equals(item.Id, _expandedItemId, StringComparison.Ordinal)));
                if ((index - startIndex + 1) % 16 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        finally
        {
            if (version == _batchVersion) _appendingRows = false;
        }
        if (version == _batchVersion) UpdateMarketStatusText();
    }

    private void UpdateMarketStatusText()
    {
        var progress = _filteredItems.Count > 0 ? $" · 已显示 {_visibleRows.Count:N0} / {_filteredItems.Count:N0}" : "";
        MarketStatusText.Text = _statusBase + progress;
    }

    private void MarketCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: MarketItemRow row }) return;
        if (FindVisualAncestor<ToggleButton>(e.OriginalSource as DependencyObject) is not null) return;
        MarketResultsList.SelectedItem = row;
        row.IsExpanded = !row.IsExpanded;
        e.Handled = true;
    }

    private void MarketDetailExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: MarketItemRow row } expander) return;
        if (_expandedRow is { } previous && !ReferenceEquals(previous, row)) previous.IsExpanded = false;
        MarketResultsList.SelectedItem = row;
        _expandedRow = row;
        _expandedItemId = row.Id;
        AnimateDetailExpander(expander, true);
    }

    private void MarketDetailExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (expander.DataContext is MarketItemRow row && string.Equals(_expandedItemId, row.Id, StringComparison.Ordinal))
        {
            _expandedItemId = null;
            if (ReferenceEquals(_expandedRow, row)) _expandedRow = null;
        }
        AnimateDetailExpander(expander, false);
    }

    private void AnimateDetailExpander(Expander expander, bool expanding)
    {
        var animationVersion = expander.Tag is int version ? version + 1 : 1;
        expander.Tag = animationVersion;
        _ = expander.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (expander.Tag is not int currentVersion || currentVersion != animationVersion || expander.IsExpanded != expanding) return;
            expander.ApplyTemplate();
            if (expander.Template.FindName("AnimatedContentHost", expander) is not Border contentHost) return;
            var translate = contentHost.RenderTransform is TranslateTransform existing ? existing.CloneCurrentValue() : new TranslateTransform();
            contentHost.RenderTransform = translate;
            var visible = contentHost.Visibility == Visibility.Visible;
            var currentHeight = visible ? Math.Max(0, contentHost.ActualHeight) : 0;
            var currentOpacity = visible ? contentHost.Opacity : 0;
            var currentOffset = visible ? translate.Y : -3;
            contentHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            contentHost.BeginAnimation(OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            if (!MotionEnabled)
            {
                SetDetailExpanderState(contentHost, translate, expanding);
                return;
            }
            if (expanding)
            {
                contentHost.Visibility = Visibility.Visible;
                contentHost.Height = double.NaN;
                contentHost.Measure(new Size(Math.Max(1, expander.ActualWidth), double.PositiveInfinity));
                var targetHeight = Math.Max(1, contentHost.DesiredSize.Height);
                contentHost.Height = currentHeight;
                contentHost.Opacity = currentOpacity;
                translate.Y = currentOffset;
                StartDetailAnimation(expander, contentHost, translate, animationVersion, currentHeight, targetHeight, currentOpacity, 1, currentOffset, 0, 190, true);
            }
            else if (currentHeight <= 0)
            {
                SetDetailExpanderState(contentHost, translate, false);
            }
            else
            {
                contentHost.Visibility = Visibility.Visible;
                contentHost.Height = currentHeight;
                StartDetailAnimation(expander, contentHost, translate, animationVersion, currentHeight, 0, currentOpacity, 0, currentOffset, -3, 155, false);
            }
        }));
    }

    private static void StartDetailAnimation(Expander expander, Border host, TranslateTransform translate, int version, double fromHeight, double toHeight, double fromOpacity, double toOpacity, double fromOffset, double toOffset, int milliseconds, bool expanded)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var easing = new CubicEase { EasingMode = expanded ? EasingMode.EaseOut : EasingMode.EaseIn };
        var height = new DoubleAnimation(fromHeight, toHeight, duration) { EasingFunction = easing };
        height.Completed += (_, _) =>
        {
            if (expander.Tag is not int current || current != version || expander.IsExpanded != expanded) return;
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.BeginAnimation(OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            SetDetailExpanderState(host, translate, expanded);
        };
        host.BeginAnimation(FrameworkElement.HeightProperty, height, HandoffBehavior.SnapshotAndReplace);
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromOffset, toOffset, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetDetailExpanderState(Border host, TranslateTransform translate, bool expanded)
    {
        host.Height = expanded ? double.NaN : 0;
        host.Opacity = expanded ? 1 : 0;
        translate.Y = expanded ? 0 : -3;
        host.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
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

    private static void SetToolButton(Button button, bool active)
    {
        button.Background = new SolidColorBrush(active ? Color.FromRgb(37, 47, 43) : Color.FromRgb(17, 23, 21));
        button.BorderBrush = new SolidColorBrush(active ? Color.FromRgb(233, 173, 80) : Color.FromRgb(49, 65, 57));
        button.Foreground = new SolidColorBrush(active ? Color.FromRgb(233, 173, 80) : Color.FromRgb(210, 220, 215));
    }

    private static bool MarketMatches(FeatureMarketItem item, string term) =>
        new[] { item.NameZh, item.ShortNameZh, item.Name, item.ShortName, item.Id }
            .Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static int MarketMatchScore(FeatureMarketItem item, string term) =>
        new[] { item.ShortNameZh, item.NameZh, item.ShortName, item.Name, item.Id }
            .Select(value => value.Equals(term, StringComparison.OrdinalIgnoreCase) ? 0 : value.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Min();

    private static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "pve" => "pve",
        "pvp-season" or "season" => "pvp-season",
        _ => "pvp"
    };

    private static string ModeDisplayName(string mode) => mode switch
    {
        "pve" => "PVE",
        "pvp-season" => "赛季服",
        _ => "PVP"
    };
}
