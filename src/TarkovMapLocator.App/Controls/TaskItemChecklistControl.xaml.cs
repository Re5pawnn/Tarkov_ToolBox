using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using TarkovMapLocator.App.Models;
using TarkovMapLocator.App.Services;
using Windows.Foundation;
using Windows.UI;

namespace TarkovMapLocator.App.Controls;

public sealed partial class TaskItemChecklistControl : UserControl
{
    private const int SearchLimit = 1000;
    private readonly DispatcherQueueTimer searchTimer;
    private readonly TaskItemChecklistService checklistService = new();
    private readonly List<TaskItemView> allItems = [];
    private readonly HashSet<string> expandedSourceItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingItemUpdates = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSourceUpdates = new(StringComparer.Ordinal);
    private LocalPathConfigService? configService;
    private string currentMode = "pvp";
    private string currentQuery = "";
    private string sortMode = "default";
    private double? updatedAt;
    private string lastError = "";
    private bool includeTasks = true;
    private bool includeHideout = true;
    private bool backendReady;
    private bool loading;
    private bool refreshing;
    private bool initialized;
    private bool suppressUiEvents;
    private int resultCount;
    private int requestId;
    private TaskItemTotals? totals;

    public TaskItemChecklistControl()
    {
        InitializeComponent();
        searchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        searchTimer.Interval = TimeSpan.FromMilliseconds(220);
        searchTimer.Tick += OnSearchTimerTick;
        SortComboBox.SelectedIndex = 0;
        UpdateResponsiveLayout();
        RenderTrackerPanel();
    }

    public ObservableCollection<TaskItemView> VisibleItems { get; } = [];

    public async Task EnsureLoadedAsync(
        LocalPathConfigService localPathConfigService,
        CancellationToken cancellationToken = default)
    {
        configService = localPathConfigService;
        if (initialized)
        {
            return;
        }

        initialized = true;
        ApplyPreferences(localPathConfigService.ReadUiPreferences());
        await LoadTrackerStateAsync(cancellationToken);
    }

    private async Task LoadTrackerStateAsync(CancellationToken cancellationToken)
    {
        loading = true;
        lastError = "";
        RenderTrackerPanel();

        try
        {
            var snapshot = await checklistService.GetStateAsync(cancellationToken);
            ApplyState(snapshot);
            await SearchTrackerAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            backendReady = false;
            allItems.Clear();
            VisibleItems.Clear();
            resultCount = 0;
            totals = null;
            lastError = ex.Message;
        }
        finally
        {
            loading = false;
            RenderTrackerPanel();
        }
    }

    private async Task SearchTrackerAsync(
        bool keepVisible = false,
        string keepItemId = "",
        bool silent = false,
        CancellationToken cancellationToken = default)
    {
        var localRequestId = ++requestId;
        if (!silent)
        {
            loading = true;
            lastError = "";
            RenderTrackerPanel();
        }

        try
        {
            var payload = await checklistService.SearchAsync(
                currentMode,
                currentQuery,
                includeTasks,
                includeHideout,
                SearchLimit,
                cancellationToken);
            if (localRequestId != requestId)
            {
                return;
            }

            backendReady = payload?.Available == true;
            updatedAt = payload?.UpdatedAt ?? updatedAt;
            lastError = payload?.Error ?? payload?.LastError ?? "";
            totals = payload?.Totals;
            resultCount = payload?.Count > 0 ? payload.Count : payload?.Items.Count ?? 0;

            allItems.Clear();
            foreach (var item in SortItems(payload?.Items ?? []))
            {
                allItems.Add(TaskItemView.FromItem(
                    item,
                    checklistService.GetMarketIconUri(item.Id),
                    pendingItemUpdates,
                    pendingSourceUpdates,
                    expandedSourceItems.Contains(item.Id)));
            }

            RefreshVisibleItems();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (localRequestId != requestId)
            {
                return;
            }

            backendReady = false;
            allItems.Clear();
            VisibleItems.Clear();
            resultCount = 0;
            totals = null;
            lastError = ex.Message;
        }
        finally
        {
            if (localRequestId == requestId)
            {
                loading = false;
                RenderTrackerPanel();
            }
        }
    }

    private async Task RefreshTrackerAsync()
    {
        refreshing = true;
        lastError = "";
        RenderTrackerPanel();

        try
        {
            var snapshot = await checklistService.RefreshAsync();
            ApplyState(snapshot);
            await SearchTrackerAsync();
        }
        catch (Exception ex)
        {
            backendReady = false;
            lastError = ex.Message;
        }
        finally
        {
            refreshing = false;
            RenderTrackerPanel();
        }
    }

    private void ApplyState(TaskItemTrackerState? snapshot)
    {
        backendReady = snapshot?.Available == true;
        updatedAt = snapshot?.UpdatedAt ?? updatedAt;
        lastError = snapshot?.Error ?? snapshot?.LastError ?? "";
    }

    private void RenderTrackerPanel()
    {
        suppressUiEvents = true;
        TasksCheckBox.IsChecked = includeTasks;
        HideoutCheckBox.IsChecked = includeHideout;
        SelectSortMode(sortMode);
        suppressUiEvents = false;

        UpdateModeButtons();
        RefreshButton.IsEnabled = !refreshing && !loading;
        SearchBox.IsEnabled = !refreshing;
        TasksCheckBox.IsEnabled = !refreshing;
        HideoutCheckBox.IsEnabled = !refreshing;
        SortComboBox.IsEnabled = !refreshing;

        var modeText = currentMode == "pve" ? "PVE" : "PVP";
        if (refreshing)
        {
            StatusText.Text = $"清单状态: 正在刷新 {modeText}";
            StatusText.Foreground = BrushResource("TrackerAccent2Brush");
        }
        else if (loading)
        {
            StatusText.Text = $"清单状态: 正在汇总 {modeText}";
            StatusText.Foreground = BrushResource("TrackerAccent2Brush");
        }
        else if (!string.IsNullOrWhiteSpace(lastError) && !backendReady)
        {
            StatusText.Text = $"清单状态: {lastError}";
            StatusText.Foreground = BrushResource("TrackerDangerBrush");
        }
        else if (!string.IsNullOrWhiteSpace(lastError))
        {
            StatusText.Text = $"清单状态: 使用缓存 / {lastError}";
            StatusText.Foreground = BrushResource("TrackerWarnBrush");
        }
        else if (backendReady)
        {
            StatusText.Text = $"清单状态: {modeText} / 更新于 {FormatUpdatedAt(updatedAt)}";
            StatusText.Foreground = BrushResource("TrackerAccentBrush");
        }
        else
        {
            StatusText.Text = "清单状态: 未加载";
            StatusText.Foreground = BrushResource("TrackerMutedBrush");
        }

        CountText.Text = $"{(resultCount > 0 ? resultCount : allItems.Count)} 个物品";
        TotalsText.Text = $"还差 {FormatNumber(totals?.Remaining ?? 0)} / 估算 {FormatNumber(totals?.EstimatedCost ?? 0)}";

        if (!backendReady && !string.IsNullOrWhiteSpace(lastError))
        {
            ShowEmpty(lastError);
            return;
        }

        if (loading || refreshing)
        {
            ShowEmpty("正在汇总物品需求...");
            return;
        }

        if (allItems.Count == 0)
        {
            ShowEmpty(string.IsNullOrWhiteSpace(currentQuery) ? "当前筛选下没有待收集物品。" : "没有找到匹配物品。");
            return;
        }

        EmptyView.Visibility = Visibility.Collapsed;
        ResultsView.Visibility = Visibility.Visible;
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyView.Visibility = Visibility.Visible;
        ResultsView.Visibility = Visibility.Collapsed;
    }

    private void RefreshVisibleItems()
    {
        VisibleItems.Clear();
        foreach (var item in allItems)
        {
            VisibleItems.Add(item);
        }
    }

    private IEnumerable<TaskItemRow> SortItems(IReadOnlyList<TaskItemRow> items)
    {
        return sortMode switch
        {
            "quantity-desc" => items
                .OrderByDescending(GetRequiredTotal)
                .ThenBy(GetSortName, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true))
                .ThenBy(static item => item.Id, StringComparer.Ordinal),
            "quantity-asc" => items
                .OrderBy(GetRequiredTotal)
                .ThenBy(GetSortName, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true))
                .ThenBy(static item => item.Id, StringComparer.Ordinal),
            "name-asc" => items
                .OrderBy(GetSortName, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true))
                .ThenBy(static item => item.Id, StringComparer.Ordinal),
            "name-desc" => items
                .OrderByDescending(GetSortName, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true))
                .ThenBy(static item => item.Id, StringComparer.Ordinal),
            _ => items
        };
    }

    private static int GetRequiredTotal(TaskItemRow item)
    {
        return item.Required + item.CompletedRequired;
    }

    private static string GetSortName(TaskItemRow item)
    {
        return FirstNonEmpty(item.Name, item.ShortName, item.Id) ?? "";
    }

    private void UpdateModeButtons()
    {
        ApplyModeButtonVisual(PvpButton, currentMode == "pvp");
        ApplyModeButtonVisual(PveButton, currentMode == "pve");
    }

    private static void ApplyModeButtonVisual(Button button, bool active)
    {
        if (active)
        {
            button.Foreground = new SolidColorBrush(Color.FromArgb(255, 8, 16, 23));
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(184, 125, 255, 115));
            button.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 141, 255, 131), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 73, 216, 91), Offset = 1 }
                }
            };
            return;
        }

        button.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 172, 186));
        button.BorderBrush = new SolidColorBrush(Colors.Transparent);
        button.Background = new SolidColorBrush(Colors.Transparent);
    }

    private void ApplyPreferences(JsonObject preferences)
    {
        suppressUiEvents = true;
        currentMode = ReadString(preferences, "trackerMode") == "pve" ? "pve" : "pvp";
        includeTasks = ReadBoolean(preferences, "trackerIncludeTasks", fallback: true);
        includeHideout = ReadBoolean(preferences, "trackerIncludeHideout", fallback: true);
        sortMode = NormalizeSortMode(ReadString(preferences, "trackerSortMode"));
        TasksCheckBox.IsChecked = includeTasks;
        HideoutCheckBox.IsChecked = includeHideout;
        SelectSortMode(sortMode);
        suppressUiEvents = false;
    }

    private void SavePreferences()
    {
        if (configService is null)
        {
            return;
        }

        var preferences = configService.ReadUiPreferences();
        preferences["trackerMode"] = currentMode;
        preferences["trackerIncludeTasks"] = includeTasks;
        preferences["trackerIncludeHideout"] = includeHideout;
        preferences["trackerSortMode"] = sortMode;
        configService.SaveUiPreferences(preferences);
    }

    private async Task ChangeModeAsync(string mode)
    {
        if (currentMode == mode || refreshing)
        {
            return;
        }

        currentMode = mode;
        SavePreferences();
        UpdateModeButtons();
        await SearchTrackerAsync();
    }

    private async Task UpdateHaveAsync(TaskItemView item, int have)
    {
        have = Math.Max(0, have);
        if (pendingItemUpdates.Contains(item.Id))
        {
            return;
        }

        pendingItemUpdates.Add(item.Id);
        try
        {
            await checklistService.UpdateItemHaveAsync(currentMode, item.Id, have);
            await SearchTrackerAsync(keepVisible: true, keepItemId: item.Id, silent: true);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            RenderTrackerPanel();
        }
        finally
        {
            pendingItemUpdates.Remove(item.Id);
        }
    }

    private async Task UpdateSourceAsync(TaskItemSourceView source)
    {
        if (pendingSourceUpdates.Contains(source.Key))
        {
            return;
        }

        pendingSourceUpdates.Add(source.Key);
        try
        {
            await checklistService.UpdateSourceCompletedAsync(currentMode, source.Key, !source.Completed);
            await SearchTrackerAsync(keepVisible: true, keepItemId: source.ItemId, silent: true);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            RenderTrackerPanel();
        }
        finally
        {
            pendingSourceUpdates.Remove(source.Key);
        }
    }

    private void SelectSortMode(string mode)
    {
        foreach (var item in SortComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.Ordinal))
            {
                SortComboBox.SelectedItem = item;
                return;
            }
        }

        SortComboBox.SelectedIndex = 0;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        currentQuery = SearchBox.Text.Trim();
        if (!initialized)
        {
            return;
        }

        searchTimer.Stop();
        searchTimer.Start();
    }

    private async void OnSearchTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await RunUiAsync(() => SearchTrackerAsync());
    }

    private async void OnPvpClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() => ChangeModeAsync("pvp"));
    }

    private async void OnPveClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() => ChangeModeAsync("pve"));
    }

    private async void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (suppressUiEvents || !initialized)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            includeTasks = TasksCheckBox.IsChecked == true;
            includeHideout = HideoutCheckBox.IsChecked == true;
            SavePreferences();
            await SearchTrackerAsync();
        });
    }

    private async void OnSortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressUiEvents || !initialized)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            sortMode = NormalizeSortMode((SortComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
            SavePreferences();
            await SearchTrackerAsync(keepVisible: true);
        });
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(RefreshTrackerAsync);
    }

    private async void OnIncrementHaveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskItemView item)
        {
            await RunUiAsync(() => UpdateHaveAsync(item, item.Have + 1));
        }
    }

    private async void OnDecrementHaveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskItemView item)
        {
            await RunUiAsync(() => UpdateHaveAsync(item, item.Have - 1));
        }
    }

    private async void OnHaveBoxLostFocus(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() => CommitHaveBoxAsync(sender));
    }

    private async void OnHaveBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await RunUiAsync(() => CommitHaveBoxAsync(sender));
        }
    }

    private async Task CommitHaveBoxAsync(object sender)
    {
        if (sender is not TextBox box || box.DataContext is not TaskItemView item)
        {
            return;
        }

        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var have))
        {
            box.Text = item.HaveText;
            return;
        }

        if (have != item.Have)
        {
            await UpdateHaveAsync(item, have);
        }
    }

    private async void OnSourceDoneClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskItemSourceView source)
        {
            await RunUiAsync(() => UpdateSourceAsync(source));
        }
    }

    private void OnToggleSourcesClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskItemView item)
        {
            return;
        }

        item.ToggleSources();
        if (item.IsSourcesExpanded)
        {
            expandedSourceItems.Add(item.Id);
        }
        else
        {
            expandedSourceItems.Remove(item.Id);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        searchTimer.Stop();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        var viewportWidth = RootScrollViewer.ViewportWidth;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = ActualWidth;
        }

        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = 1600;
        }

        var appWidth = Math.Max(0, Math.Min(1600, viewportWidth - 24));
        AppRoot.Width = appWidth + 24;
        AppRoot.Margin = new Thickness(Math.Max(0, (viewportWidth - appWidth - 24) / 2), 0, 0, 0);

        if (ActualHeight > 0)
        {
            TrackerPanel.MinHeight = Math.Min(ActualHeight * 0.72, 720);
        }

        var contentWidth = appWidth - 24;
        var compactToolbar = contentWidth <= 900;
        if (compactToolbar)
        {
            MoveToolbarElement(SearchField, 0, 0, 5);
            MoveToolbarElement(ModeToggle, 1, 0, 5);
            MoveToolbarElement(FilterGroup, 2, 0, 5);
            MoveToolbarElement(SortField, 3, 0, 5);
            MoveToolbarElement(RefreshButton, 4, 0, 5);
            SearchBox.MinWidth = 0;
            ModeToggle.HorizontalAlignment = HorizontalAlignment.Stretch;
            FilterGroup.HorizontalAlignment = HorizontalAlignment.Stretch;
            SortComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            RefreshButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            MoveToolbarElement(SearchField, 0, 0, 1);
            MoveToolbarElement(ModeToggle, 0, 1, 1);
            MoveToolbarElement(FilterGroup, 0, 2, 1);
            MoveToolbarElement(SortField, 0, 3, 1);
            MoveToolbarElement(RefreshButton, 0, 4, 1);
            SearchBox.MinWidth = 260;
            ModeToggle.HorizontalAlignment = HorizontalAlignment.Left;
            FilterGroup.HorizontalAlignment = HorizontalAlignment.Left;
            SortComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            RefreshButton.HorizontalAlignment = HorizontalAlignment.Left;
        }

        ResultsLayout.MinItemWidth = contentWidth switch
        {
            <= 540 => Math.Max(220, contentWidth),
            <= 980 => Math.Max(320, (contentWidth - 12) / 2),
            <= 1320 => 360,
            _ => 400
        };
    }

    private static void MoveToolbarElement(FrameworkElement element, int row, int column, int columnSpan)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private Brush BrushResource(string key)
    {
        return (Brush)Resources[key];
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
            backendReady = false;
            loading = false;
            refreshing = false;
            lastError = ex.Message;
            RenderTrackerPanel();
        }
    }

    private static string NormalizeSortMode(string? value)
    {
        return value is "quantity-desc" or "quantity-asc" or "name-asc" or "name-desc"
            ? value
            : "default";
    }

    private static string FormatUpdatedAt(double? timestampSec)
    {
        if (timestampSec is null or <= 0 || double.IsNaN(timestampSec.Value))
        {
            return "未更新";
        }

        try
        {
            var milliseconds = (long)Math.Round(timestampSec.Value * 1000);
            var date = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
            return date.ToString("yyyy/M/d HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"));
        }
        catch
        {
            return "未更新";
        }
    }

    private static string FormatNumber(long value)
    {
        return value.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.Select(static value => value?.Trim()).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? ReadString(JsonObject payload, string propertyName)
    {
        try
        {
            return payload[propertyName]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBoolean(JsonObject payload, string propertyName, bool fallback)
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
}

public sealed class TaskItemView : INotifyPropertyChanged
{
    private static readonly Dictionary<string, ImageSource> IconSourceCache = new(StringComparer.Ordinal);
    private const int CollapsedSourceLimit = 5;
    private bool isSourcesExpanded;

    private TaskItemView(
        TaskItemRow row,
        ImageSource? iconSource,
        IReadOnlyList<TaskItemSourceView> sources,
        bool isPending,
        bool isSourcesExpanded)
    {
        Id = row.Id;
        DisplayName = FirstNonEmpty(row.Name, row.ShortName, row.Id) ?? "未知物品";
        Subtitle = FirstNonEmpty(row.ShortName, row.Id) ?? "--";
        Have = Math.Max(0, row.Have);
        HaveText = Have.ToString(CultureInfo.InvariantCulture);
        var totalRequired = row.Required + row.CompletedRequired;
        ProgressHaveText = FormatNumber(row.Have + row.CompletedRequired);
        ProgressTotalText = $"/ {FormatNumber(totalRequired > 0 ? totalRequired : row.Required)}";
        FoundInRaidText = $"(带勾 {FormatNumber(row.FoundInRaidRequired)})";
        FoundInRaidVisibility = row.FoundInRaidRequired > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskTotalText = $"任务 {FormatNumber(row.TaskRequired + row.CompletedTaskRequired)}";
        HideoutTotalText = $"藏身处 {FormatNumber(row.HideoutRequired + row.CompletedHideoutRequired)}";
        FoundInRaidChipText = $"带勾 {FormatNumber(row.FoundInRaidRequired)}";
        IconSource = iconSource;
        IconVisibility = iconSource is null ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderVisibility = iconSource is null ? Visibility.Visible : Visibility.Collapsed;
        Sources = sources;
        SourceCountText = $"{sources.Count} 来源";
        IsNotPending = !isPending;
        this.isSourcesExpanded = isSourcesExpanded && sources.Count > CollapsedSourceLimit;
        RefreshSourcePresentation();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Subtitle { get; }

    public int Have { get; }

    public string HaveText { get; }

    public string ProgressHaveText { get; }

    public string ProgressTotalText { get; }

    public string FoundInRaidText { get; }

    public Visibility FoundInRaidVisibility { get; }

    public string TaskTotalText { get; }

    public string HideoutTotalText { get; }

    public string FoundInRaidChipText { get; }

    public ImageSource? IconSource { get; }

    public Visibility IconVisibility { get; }

    public Visibility PlaceholderVisibility { get; }

    public IReadOnlyList<TaskItemSourceView> Sources { get; }

    public IReadOnlyList<TaskItemSourceView> VisibleSources { get; private set; } = [];

    public string SourceCountText { get; }

    public string SourcesHeaderText { get; private set; } = "";

    public string MoreSourcesText { get; private set; } = "";

    public Visibility MoreSourcesVisibility { get; private set; }

    public string ToggleSourcesButtonText { get; private set; } = "";

    public Visibility ToggleSourcesButtonVisibility { get; private set; }

    public bool IsSourcesExpanded => isSourcesExpanded;

    public bool IsNotPending { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static TaskItemView FromItem(
        TaskItemRow row,
        Uri iconUri,
        IReadOnlySet<string> pendingItems,
        IReadOnlySet<string> pendingSources,
        bool isSourcesExpanded)
    {
        ImageSource? iconSource = IsMarketItemId(row.Id) ? GetIconSource(iconUri) : null;
        var sources = (row.Sources ?? [])
            .Select(source => TaskItemSourceView.FromSource(row.Id, source, pendingSources.Contains(source.Key)))
            .ToArray();
        return new TaskItemView(row, iconSource, sources, pendingItems.Contains(row.Id), isSourcesExpanded);
    }

    public void ToggleSources()
    {
        if (Sources.Count <= CollapsedSourceLimit)
        {
            return;
        }

        isSourcesExpanded = !isSourcesExpanded;
        RefreshSourcePresentation();
        OnPropertyChanged(nameof(IsSourcesExpanded));
    }

    private void RefreshSourcePresentation()
    {
        var total = Sources.Count;
        var hiddenCount = Math.Max(0, total - CollapsedSourceLimit);
        VisibleSources = isSourcesExpanded || hiddenCount == 0
            ? Sources
            : Sources.Take(CollapsedSourceLimit).ToArray();
        SourcesHeaderText = total == 0 ? "来源" : $"来源 ({total})";
        MoreSourcesText = hiddenCount > 0 && !isSourcesExpanded
            ? $"还有 {hiddenCount} 条来源"
            : "";
        MoreSourcesVisibility = string.IsNullOrWhiteSpace(MoreSourcesText) ? Visibility.Collapsed : Visibility.Visible;
        ToggleSourcesButtonText = isSourcesExpanded ? "收起" : "展开全部";
        ToggleSourcesButtonVisibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        OnPropertyChanged(nameof(VisibleSources));
        OnPropertyChanged(nameof(SourcesHeaderText));
        OnPropertyChanged(nameof(MoreSourcesText));
        OnPropertyChanged(nameof(MoreSourcesVisibility));
        OnPropertyChanged(nameof(ToggleSourcesButtonText));
        OnPropertyChanged(nameof(ToggleSourcesButtonVisibility));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static ImageSource GetIconSource(Uri iconUri)
    {
        var key = iconUri.AbsoluteUri;
        if (IconSourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = new BitmapImage(iconUri);
        IconSourceCache[key] = source;
        return source;
    }

    private static bool IsMarketItemId(string value)
    {
        return value.Length == 24 && value.All(Uri.IsHexDigit);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.Select(static value => value?.Trim()).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string FormatNumber(long value)
    {
        return value.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"));
    }
}

public sealed class TaskItemSourceView
{
    private TaskItemSourceView(
        string itemId,
        string key,
        bool completed,
        string displayText,
        bool isPending)
    {
        ItemId = itemId;
        Key = key;
        Completed = completed;
        DisplayText = displayText;
        CheckText = completed ? "✓" : "";
        TextBrush = new SolidColorBrush(completed ? Color.FromArgb(255, 223, 255, 224) : Color.FromArgb(255, 217, 224, 229));
        DoneButtonText = completed ? "取消" : "完成";
        DoneButtonBrush = new SolidColorBrush(completed ? Color.FromArgb(97, 116, 78, 24) : Color.FromArgb(97, 42, 115, 54));
        DoneButtonBorderBrush = new SolidColorBrush(completed ? Color.FromArgb(133, 255, 197, 90) : Color.FromArgb(148, 125, 255, 115));
        DoneButtonForeground = new SolidColorBrush(completed ? Color.FromArgb(255, 255, 226, 167) : Color.FromArgb(255, 223, 255, 224));
        IsNotPending = !isPending;
    }

    public string ItemId { get; }

    public string Key { get; }

    public bool Completed { get; }

    public string DisplayText { get; }

    public string CheckText { get; }

    public Brush TextBrush { get; }

    public string DoneButtonText { get; }

    public Brush DoneButtonBrush { get; }

    public Brush DoneButtonBorderBrush { get; }

    public Brush DoneButtonForeground { get; }

    public bool IsNotPending { get; }

    public static TaskItemSourceView FromSource(string itemId, TaskItemSource source, bool isPending)
    {
        return new TaskItemSourceView(
            itemId,
            source.Key,
            source.Completed,
            FormatSource(source),
            isPending);
    }

    private static string FormatSource(TaskItemSource source)
    {
        var count = source.Count > 0 ? $"需求{FormatNumber(source.Count)}" : "";
        if (string.Equals(source.Type, "hideout", StringComparison.OrdinalIgnoreCase))
        {
            var name = SafeText(source.Name, "藏身处");
            var levelText = ExtractHideoutLevel(source.Detail);
            return string.Join(" ", new[] { "[藏身处]", name, levelText, count }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        var trader = SafeText(source.Trader, "任务");
        var taskName = SafeText(source.Name, "任务");
        var detail = source.Choice ? SafeText(source.Detail, "") : "";
        return string.Join(" ", new[] { $"[{trader}]", taskName, detail, count }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ExtractHideoutLevel(string detail)
    {
        var text = detail ?? "";
        var index = text.IndexOf('级', StringComparison.Ordinal);
        if (index <= 0)
        {
            return "";
        }

        var start = index - 1;
        while (start > 0 && char.IsDigit(text[start - 1]))
        {
            start--;
        }

        var number = text[start..index];
        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? $"Lv.{number}"
            : "";
    }

    private static string SafeText(string? value, string fallback)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string FormatNumber(long value)
    {
        return value.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"));
    }
}
