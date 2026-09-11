using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.TaskItems;

public partial class TaskItemsView : UserControl
{
    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;
    private readonly IFeatureHost _host;
    private readonly ObservableCollection<FeatureTrackerItem> _visibleItems = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly HashSet<string> _pendingItemIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Mode, int Have)> _queuedItemWrites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingSourceKeys = new(StringComparer.Ordinal);
    private string _mode = "pvp";
    private bool _includeTasks = true;
    private bool _includeHideout = true;
    private bool _showCompleted;
    private bool _automaticRefreshRunning;
    private int _loadRequestId;
    private string? _renderKey;

    public TaskItemsView(IFeatureHost host)
    {
        _host = host;
        InitializeComponent();
        AsyncItemIcon.SetFeatureHost(this, host);
        TaskItemsResultsList.ItemsSource = _visibleItems;
        _searchTimer.Tick += SearchTimer_Tick;
        Loaded += TaskItemsView_Loaded;
        Unloaded += (_, _) =>
        {
            _searchTimer.Stop();
            _loadRequestId++;
        };
        IsVisibleChanged += TaskItemsView_IsVisibleChanged;
    }

    private void TaskItemsView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateResults(forceRebuild: true);
        _ = StartAutomaticRefreshAsync();
    }

    private void TaskItemsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && IsLoaded)
        {
            UpdateResults();
            _ = StartAutomaticRefreshAsync();
        }
    }

    private void TaskItemsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        UpdateResults();
    }

    private void TaskItemsPvp_Click(object sender, RoutedEventArgs e) { _mode = "pvp"; _renderKey = null; UpdateResults(); }
    private void TaskItemsPve_Click(object sender, RoutedEventArgs e) { _mode = "pve"; _renderKey = null; UpdateResults(); }
    private void TrackerTasks_Click(object sender, RoutedEventArgs e) { _includeTasks = !_includeTasks; UpdateResults(); }
    private void TrackerHideout_Click(object sender, RoutedEventArgs e) { _includeHideout = !_includeHideout; UpdateResults(); }
    private void TrackerCompleted_Click(object sender, RoutedEventArgs e) { _showCompleted = !_showCompleted; UpdateResults(); }

    private void TrackerHideoutLevels_Click(object sender, RoutedEventArgs e)
    {
        var stations = _host.GetHideoutStations(_mode);
        if (stations.Count == 0)
        {
            _host.ShowNotification("请先刷新任务物品清单");
            return;
        }

        var dialog = new HideoutLevelWindow(_mode, stations) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        _host.SetHideoutLevels(_mode, dialog.Levels);
        _renderKey = null;
        UpdateResults(forceRebuild: true);
        _host.ShowNotification("藏身处等级已保存");
    }

    private async Task StartAutomaticRefreshAsync()
    {
        if (_automaticRefreshRunning) return;
        _automaticRefreshRunning = true;
        try
        {
            TaskItemsStatusText.Text = _visibleItems.Count > 0 ? "正在检查任务物品数据更新…" : "正在加载…";
            await _host.EnsureTaskItemsFreshAsync(_host.ShutdownToken);
            _renderKey = null;
            UpdateResults();
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _host.WriteLog(FeatureLogLevel.Error, "任务清单", "后台任务物品刷新失败", exception.ToString());
            UpdateResults();
            TaskItemsStatusText.Text = $"自动更新失败：{exception.Message}";
        }
        finally
        {
            _automaticRefreshRunning = false;
        }
    }

    private async void TaskItemsRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_automaticRefreshRunning)
        {
            _host.ShowNotification("任务物品数据正在后台刷新");
            return;
        }

        _loadRequestId++;
        TaskItemsRefreshButton.IsEnabled = false;
        TaskItemsStatusText.Text = "正在刷新任务和藏身处物品需求…";
        _host.WriteLog(FeatureLogLevel.Info, "任务清单", "用户手动刷新任务物品数据", $"模式: {_mode.ToUpperInvariant()}\n包含任务: {_includeTasks}\n包含藏身处: {_includeHideout}");
        try
        {
            await _host.RefreshTaskItemsAsync(_host.ShutdownToken);
            _renderKey = null;
            UpdateResults(forceRebuild: true);
            _host.ShowNotification("任务物品清单已刷新");
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _host.WriteLog(FeatureLogLevel.Error, "任务清单", "手动任务物品刷新失败", exception.ToString());
            UpdateResults();
            TaskItemsStatusText.Text = $"刷新清单失败：{exception.Message}";
            _host.ShowNotification("任务物品清单刷新失败，已显示具体原因");
        }
        finally
        {
            TaskItemsRefreshButton.IsEnabled = true;
        }
    }

    private void UpdateResults(bool forceRebuild = false)
    {
        if (TaskItemsResultsList is null) return;
        SetToolButton(TaskItemsPvpButton, _mode == "pvp");
        SetToolButton(TaskItemsPveButton, _mode == "pve");
        SetToolButton(TrackerTasksButton, _includeTasks);
        SetToolButton(TrackerHideoutButton, _includeHideout);
        SetToolButton(TrackerCompletedButton, _showCompleted);
        _searchTimer.Stop();
        var request = CaptureQuery();
        var requestId = ++_loadRequestId;
        _ = LoadResultsAsync(request, requestId, forceRebuild, null);
    }

    private void RefreshItem(string itemId)
    {
        if (!IsVisible) return;
        var request = CaptureQuery();
        var requestId = ++_loadRequestId;
        _ = LoadResultsAsync(request, requestId, false, itemId);
    }

    private async Task LoadResultsAsync(TrackerQuery request, int requestId, bool forceRebuild, string? updatedItemId)
    {
        FeatureTaskTrackerSnapshot load;
        try
        {
            load = await Task.Run(() => _host.GetTaskItems(request.Mode, request.Query, request.IncludeTasks, request.IncludeHideout, request.ShowCompleted));
        }
        catch (Exception exception)
        {
            if (IsCurrentRequest(request, requestId)) TaskItemsStatusText.Text = $"加载任务物品清单失败：{exception.Message}";
            return;
        }

        if (!IsCurrentRequest(request, requestId)) return;
        ApplyStatus(load);
        var renderKey = BuildRenderKey(load, request);
        if (!string.IsNullOrWhiteSpace(updatedItemId))
        {
            var replacement = load.Items.FirstOrDefault(item => string.Equals(item.Id, updatedItemId, StringComparison.Ordinal));
            var index = IndexOfItem(updatedItemId);
            if (replacement is not null && index >= 0 && load.Items.Count == _visibleItems.Count)
            {
                _visibleItems[index] = replacement;
                _renderKey = renderKey;
                TaskItemsEmptyText.Visibility = Visibility.Collapsed;
                return;
            }
        }

        if (!forceRebuild && string.Equals(_renderKey, renderKey, StringComparison.Ordinal)) return;
        _renderKey = renderKey;
        ReplaceVisibleItems(load.Items);
        if (load.Items.Count == 0)
        {
            TaskItemsEmptyText.Text = load.ErrorMessage is null ? "没有符合当前筛选条件的物品。" : load.ErrorMessage;
            TaskItemsEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            TaskItemsEmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private TrackerQuery CaptureQuery() => new(_mode, TaskItemsSearchBox?.Text.Trim() ?? "", _includeTasks, _includeHideout, _showCompleted);

    private bool IsCurrentRequest(TrackerQuery request, int requestId) =>
        IsVisible && requestId == _loadRequestId && request == CaptureQuery();

    private void ReplaceVisibleItems(IReadOnlyList<FeatureTrackerItem> items)
    {
        _visibleItems.Clear();
        foreach (var item in items) _visibleItems.Add(item);
    }

    private void ReplaceVisibleItem(FeatureTrackerItem replacement)
    {
        var index = IndexOfItem(replacement.Id);
        if (index >= 0) _visibleItems[index] = replacement;
    }

    private int IndexOfItem(string itemId)
    {
        for (var index = 0; index < _visibleItems.Count; index++)
            if (string.Equals(_visibleItems[index].Id, itemId, StringComparison.Ordinal)) return index;
        return -1;
    }

    private void ApplyStatus(FeatureTaskTrackerSnapshot load) =>
        TaskItemsStatusText.Text = load.ErrorMessage is null
            ? $"{(_mode == "pve" ? "PVE" : "PVP")} · {(load.IsStale ? "缓存超过 24 小时" : "缓存有效")} · {load.Items.Count:N0} 项 / 仍需 {load.TotalRemaining:N0} 件 / 预算 {FormatPrice(load.TotalEstimatedCost)} · 数据 {load.UpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "未知"}"
            : load.ErrorMessage;

    private async void TrackerCountDecrease_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FeatureTrackerItem item } button) return;
        button.IsEnabled = false;
        try { await ChangeItemCountAsync(item, -1); }
        finally { button.IsEnabled = true; }
    }

    private async void TrackerCountIncrease_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FeatureTrackerItem item } button) return;
        button.IsEnabled = false;
        try { await ChangeItemCountAsync(item, 1); }
        finally { button.IsEnabled = true; }
    }

    private async void TrackerSourceCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: FeatureTrackerRequirementSource source } checkBox) return;
        var mode = _mode;
        var pendingKey = string.Concat(mode, "\u001f", source.Key);
        if (!_pendingSourceKeys.Add(pendingKey)) return;
        var completed = checkBox.IsChecked == true;
        var owner = _visibleItems.FirstOrDefault(item => item.Sources.Any(candidate => string.Equals(candidate.Key, source.Key, StringComparison.Ordinal)));
        checkBox.IsEnabled = false;
        try
        {
            await Task.Run(() => _host.SetTaskItemSourceCompleted(mode, source.Key, completed));
            if (IsVisible && string.Equals(_mode, mode, StringComparison.Ordinal))
            {
                if (owner is null) { _renderKey = null; UpdateResults(forceRebuild: true); }
                else RefreshItem(owner.Id);
            }
        }
        catch (Exception exception)
        {
            TaskItemsStatusText.Text = $"更新需求来源失败：{exception.Message}";
            _renderKey = null;
            UpdateResults(forceRebuild: true);
        }
        finally
        {
            _pendingSourceKeys.Remove(pendingKey);
            checkBox.IsEnabled = true;
        }
    }

    private async Task ChangeItemCountAsync(FeatureTrackerItem item, int delta)
    {
        var current = _visibleItems.FirstOrDefault(candidate => string.Equals(candidate.Id, item.Id, StringComparison.Ordinal)) ?? item;
        var have = (int)Math.Clamp((long)current.Have + delta, 0, int.MaxValue);
        await SetItemCountAsync(current, have);
    }

    private async Task SetItemCountAsync(FeatureTrackerItem item, int have)
    {
        var mode = _mode;
        have = Math.Max(0, have);
        var pendingKey = string.Concat(mode, "\u001f", item.Id);
        ReplaceVisibleItem(item with { Have = have });
        _queuedItemWrites[pendingKey] = (mode, have);
        if (!_pendingItemIds.Add(pendingKey)) return;
        try
        {
            while (_queuedItemWrites.Remove(pendingKey, out var pendingWrite))
                await Task.Run(() => _host.SetTaskItemCount(pendingWrite.Mode, item.Id, pendingWrite.Have));
            if (IsVisible && string.Equals(_mode, mode, StringComparison.Ordinal)) RefreshItem(item.Id);
        }
        catch (Exception exception)
        {
            _queuedItemWrites.Remove(pendingKey);
            TaskItemsStatusText.Text = $"更新持有数量失败：{exception.Message}";
            _renderKey = null;
            UpdateResults(forceRebuild: true);
        }
        finally
        {
            _pendingItemIds.Remove(pendingKey);
        }
    }

    private void TrackerCountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        UpdateTrackerCount(sender as TextBox);
        e.Handled = true;
    }

    private void TrackerCountBox_LostFocus(object sender, RoutedEventArgs e) => UpdateTrackerCount(sender as TextBox);

    private async void UpdateTrackerCount(TextBox? box)
    {
        if (box?.Tag is not FeatureTrackerItem item || !int.TryParse(box.Text, out var have)) return;
        have = Math.Max(0, have);
        var current = _visibleItems.FirstOrDefault(candidate => string.Equals(candidate.Id, item.Id, StringComparison.Ordinal)) ?? item;
        if (have == current.Have) return;
        box.IsEnabled = false;
        try { await SetItemCountAsync(current, have); }
        finally { box.IsEnabled = true; }
    }

    private static string BuildRenderKey(FeatureTaskTrackerSnapshot load, TrackerQuery request) => string.Join(
        "\u001f", request.Mode, request.Query, request.IncludeTasks, request.IncludeHideout, request.ShowCompleted,
        load.UpdatedAt?.ToUnixTimeSeconds() ?? 0, load.StateVersion);

    private void AnimatedDetailExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander) AnimateDetailExpander(expander, true);
    }

    private void AnimatedDetailExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander) AnimateDetailExpander(expander, false);
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

    private static string FormatPrice(long value) => $"{value:N0} ₽";

    private readonly record struct TrackerQuery(string Mode, string Query, bool IncludeTasks, bool IncludeHideout, bool ShowCompleted);
}
