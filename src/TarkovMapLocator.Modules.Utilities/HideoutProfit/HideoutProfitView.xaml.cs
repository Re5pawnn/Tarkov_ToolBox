using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.Utilities.Controls;

namespace TarkovMapLocator.Modules.Utilities.HideoutProfit;

public partial class HideoutProfitView : UserControl
{
    private readonly IFeatureHost _host;
    private readonly Action _navigateBack;
    private IReadOnlyDictionary<string, FeatureMarketItem> _market = new Dictionary<string, FeatureMarketItem>();
    private string _mode = "pvp";
    private bool _loading;
    private bool _updatingFilters;
    private HideoutProfitSortField _sortField = HideoutProfitSortField.ProfitPerHour;
    private bool _sortDescending = true;

    public HideoutProfitView(IFeatureHost host, Action navigateBack)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _navigateBack = navigateBack ?? throw new ArgumentNullException(nameof(navigateBack));
        InitializeComponent();
        ModuleItemIcon.SetFeatureHost(this, host);
        _ = InitializeAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _navigateBack();
    private void Pvp_Click(object sender, RoutedEventArgs e) => SetMode("pvp");
    private void Pve_Click(object sender, RoutedEventArgs e) => SetMode("pve");
    private void Season_Click(object sender, RoutedEventArgs e) => SetMode("pvp-season");

    private void SetMode(string mode)
    {
        if (string.Equals(_mode, mode, StringComparison.OrdinalIgnoreCase)) return;
        _mode = mode;
        _market = _host.GetMarketItems(_mode);
        RefreshStationOptions(resetSelection: true);
        UpdateResults();
    }

    private void StationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilters) UpdateResults();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateResults();
    private void DurationSort_Click(object sender, RoutedEventArgs e) => SetSort(HideoutProfitSortField.Duration);
    private void ValueSort_Click(object sender, RoutedEventArgs e) => SetSort(HideoutProfitSortField.Profit);
    private void HourlySort_Click(object sender, RoutedEventArgs e) => SetSort(HideoutProfitSortField.ProfitPerHour);

    private void SetSort(HideoutProfitSortField field)
    {
        if (_sortField == field) _sortDescending = !_sortDescending;
        else
        {
            _sortField = field;
            _sortDescending = field != HideoutProfitSortField.Duration;
        }
        UpdateResults();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在刷新配方与三种模式行情…";
        try
        {
            var profitRefresh = HideoutProfitService.RefreshAsync(_host.ShutdownToken);
            var marketRefresh = _host.RefreshMarketAsync(_host.ShutdownToken);
            await Task.WhenAll(profitRefresh, marketRefresh);
            _market = _host.GetMarketItems(_mode);
            RefreshStationOptions(resetSelection: false);
            UpdateResults();
            var result = await profitRefresh;
            if (result.IsComplete)
            {
                _host.ShowNotification("藏身处利润数据已刷新");
                _host.WriteLog(FeatureLogLevel.Info, "藏身处利润", "用户手动刷新完成", $"模式: {ModeDisplayName(_mode)}");
            }
            else
            {
                var notice = result.HasUpdates ? "部分数据源更新失败，已保留对应旧缓存" : "数据源更新失败，已继续使用旧缓存";
                _host.ShowNotification(notice);
                StatusText.Text += $" · {notice}";
                _host.WriteLog(FeatureLogLevel.Warning, "藏身处利润", notice, string.Join(Environment.NewLine, result.Failures));
            }
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            UpdateResults();
            StatusText.Text += $" · 刷新失败：{exception.Message}";
            _host.WriteLog(FeatureLogLevel.Error, "藏身处利润", "刷新配方或行情失败", exception.ToString());
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task InitializeAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        _market = _host.GetMarketItems(_mode);
        RefreshStationOptions(resetSelection: false);
        UpdateResults();
        try
        {
            await Task.WhenAll(
                _host.EnsureMarketFreshAsync(_host.ShutdownToken),
                HideoutProfitService.EnsureFreshAsync(_host.ShutdownToken));
            _market = _host.GetMarketItems(_mode);
            RefreshStationOptions(resetSelection: false);
            UpdateResults();
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RefreshStationOptions(resetSelection: false);
            UpdateResults();
            StatusText.Text += $" · 更新失败，保留缓存：{exception.Message}";
            _host.WriteLog(FeatureLogLevel.Warning, "藏身处利润", "自动更新失败，继续使用本地缓存", exception.Message);
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void RefreshStationOptions(bool resetSelection)
    {
        var currentId = resetSelection ? "" : StationSelector.SelectedValue as string ?? "";
        var load = HideoutProfitService.Load(_mode, _market);
        var options = new[] { new HideoutProfitStationOption("", "全部设施") }.Concat(load.Stations).ToArray();
        _updatingFilters = true;
        StationSelector.ItemsSource = options;
        StationSelector.SelectedValue = options.Any(option => option.Id == currentId) ? currentId : "";
        if (StationSelector.SelectedIndex < 0) StationSelector.SelectedIndex = 0;
        _updatingFilters = false;
    }

    private void UpdateResults()
    {
        SetToolButton(PvpButton, _mode == "pvp");
        SetToolButton(PveButton, _mode == "pve");
        SetToolButton(SeasonButton, _mode == "pvp-season");
        var stationId = StationSelector.SelectedValue as string ?? "";
        var load = HideoutProfitService.Load(_mode, _market, stationId, SearchBox?.Text ?? "");
        var rows = HideoutProfitService.SortRows(load.Rows, _sortField, _sortDescending);
        ResultsList.ItemsSource = rows;
        UpdateSortButtons();
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = load.ErrorMessage is { Length: > 0 } error
            ? error
            : $"{ModeDisplayName(_mode)} · {rows.Count:N0} / {load.TotalCrafts:N0} 条配方 · 可计算 {load.CalculableCrafts:N0} 条 · " +
              $"排序 {SortDescription()} · 更新 {load.UpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "未知"}" +
              (load.IsStale ? " · 缓存超过 12 小时" : "");
    }

    private void UpdateSortButtons()
    {
        DurationSortButton.Content = SortLabel(HideoutProfitSortField.Duration, "制造时间");
        ValueSortButton.Content = SortLabel(HideoutProfitSortField.Profit, "利润");
        HourlySortButton.Content = SortLabel(HideoutProfitSortField.ProfitPerHour, "利润每小时");
        SetToolButton(DurationSortButton, _sortField == HideoutProfitSortField.Duration);
        SetToolButton(ValueSortButton, _sortField == HideoutProfitSortField.Profit);
        SetToolButton(HourlySortButton, _sortField == HideoutProfitSortField.ProfitPerHour);
    }

    private string SortLabel(HideoutProfitSortField field, string label) =>
        _sortField == field ? $"{label} {(_sortDescending ? "↓" : "↑")}" : $"{label} ↕";

    private string SortDescription()
    {
        var field = _sortField switch
        {
            HideoutProfitSortField.Duration => "制造时间",
            HideoutProfitSortField.Profit => "利润",
            _ => "每小时利润"
        };
        return $"{field}{(_sortDescending ? "从高到低" : "从低到高")}";
    }

    private static string ModeDisplayName(string mode) => mode switch
    {
        "pve" => "PVE",
        "pvp-season" => "赛季服",
        _ => "PVP"
    };

    private void SetToolButton(Button button, bool active)
    {
        button.Background = active ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent;
        button.BorderBrush = active ? (Brush)FindResource("LineBrightBrush") : Brushes.Transparent;
        button.Foreground = active ? (Brush)FindResource("AmberBrush") : (Brush)FindResource("TextDimBrush");
    }
}
