using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Memo;

public partial class MemoView : UserControl, IFeatureViewLifecycle
{
    private readonly IFeatureHost _host;
    private readonly MemoStateStore _store;
    private readonly ObservableCollection<MemoItem> _items = [];
    private readonly ObservableCollection<MemoSearchResult> _searchResults = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private MemoState _state;
    private MemoOverlayWindow? _overlay;
    private bool _suppressChanges;
    private bool _editOverlay;
    private bool _disposed;

    public MemoView(IFeatureHost host)
    {
        _host = host;
        _store = new MemoStateStore(host);
        _state = _store.Load();
        InitializeComponent();

        _suppressChanges = true;
        MemoTextBox.Text = _state.Text;
        OverlayVisibleCheckBox.IsChecked = _state.OverlayVisible;
        foreach (var item in _state.Items) _items.Add(item);
        _suppressChanges = false;
        SelectedItemsList.ItemsSource = _items;
        ItemSearchResultsList.ItemsSource = _searchResults;
        UpdateEmptyState();

        _searchTimer.Tick += SearchTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _host.MarketDataChanged += Host_MarketDataChanged;
        Loaded += MemoView_Loaded;
    }

    public void OnActivated() => _ = EnsureItemsAsync();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _searchTimer.Stop();
        _saveTimer.Stop();
        _host.MarketDataChanged -= Host_MarketDataChanged;
        SaveState();
        _overlay?.ClosePermanently();
        _overlay = null;
        return ValueTask.CompletedTask;
    }

    private void MemoView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateOverlay();
        _ = EnsureItemsAsync();
    }

    private async Task EnsureItemsAsync()
    {
        try
        {
            if (_host.GetMarketItems(_host.CurrentMarketMode).Count == 0)
                await _host.EnsureMarketFreshAsync(_host.ShutdownToken);
            if (!string.IsNullOrWhiteSpace(ItemSearchBox.Text)) UpdateSearchResults();
        }
        catch (OperationCanceledException) when (_host.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ItemSearchStatusText.Text = $"物品目录加载失败：{exception.Message}";
            _host.WriteLog(FeatureLogLevel.Warning, "备忘录", "物品目录加载失败", exception.ToString());
        }
    }

    private void Host_MarketDataChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateSearchResults);

    private void MemoTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChanges) return;
        SaveStatusText.Text = "正在保存";
        QueueSave();
        UpdateOverlay();
    }

    private void ItemSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        UpdateSearchResults();
    }

    private void UpdateSearchResults()
    {
        if (_disposed || ItemSearchBox is null) return;
        var query = ItemSearchBox.Text.Trim();
        _searchResults.Clear();
        if (query.Length == 0)
        {
            ItemSearchStatusText.Text = "输入名称搜索物品";
            return;
        }

        var matches = _host.GetMarketItems(_host.CurrentMarketMode).Values
            .Where(item => Matches(item, query))
            .OrderByDescending(item => StartsWith(item, query))
            .ThenBy(item => DisplayName(item), StringComparer.CurrentCultureIgnoreCase)
            .Take(40)
            .Select(item => new MemoSearchResult(
                item.Id,
                DisplayName(item),
                FirstNotEmpty(item.ShortNameZh, item.ShortName, item.Name),
                item.IconLink));
        foreach (var item in matches) _searchResults.Add(item);
        ItemSearchStatusText.Text = _searchResults.Count == 0 ? "没有找到匹配物品" : $"找到 {_searchResults.Count} 项";
    }

    private void AddItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemoSearchResult result }) return;
        var existing = _items.FirstOrDefault(item => string.Equals(item.Id, result.Id, StringComparison.Ordinal));
        if (existing is null)
            _items.Add(new MemoItem(result.Id, result.Name, result.ShortName, result.IconLink, 1));
        else
            ReplaceItem(existing, existing.Quantity + 1);
        ItemsChanged();
    }

    private void DecreaseItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MemoItem item }) ReplaceItem(item, Math.Max(1, item.Quantity - 1));
        ItemsChanged();
    }

    private void IncreaseItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MemoItem item }) ReplaceItem(item, Math.Min(9_999, item.Quantity + 1));
        ItemsChanged();
    }

    private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MemoItem item }) _items.Remove(item);
        ItemsChanged();
    }

    private void ItemQuantityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyQuantity(sender as TextBox);
        Keyboard.ClearFocus();
    }

    private void ItemQuantityBox_LostFocus(object sender, RoutedEventArgs e) => ApplyQuantity(sender as TextBox);

    private void ApplyQuantity(TextBox? box)
    {
        if (box?.Tag is not MemoItem item) return;
        var quantity = int.TryParse(box.Text, out var parsed) ? Math.Clamp(parsed, 1, 9_999) : item.Quantity;
        ReplaceItem(item, quantity);
        ItemsChanged();
    }

    private void ReplaceItem(MemoItem item, int quantity)
    {
        var index = _items.IndexOf(item);
        if (index >= 0) _items[index] = item with { Quantity = quantity };
    }

    private void ItemsChanged()
    {
        UpdateEmptyState();
        QueueSave();
        UpdateOverlay();
    }

    private void UpdateEmptyState() =>
        SelectedItemsEmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OverlayVisibleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChanges) return;
        _editOverlay = false;
        QueueSave();
        UpdateOverlay();
    }

    private void AdjustOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayVisibleCheckBox.IsChecked != true || !HasMemoContent())
        {
            _host.ShowNotification("请先填写文字或添加物品，并开启悬浮窗");
            return;
        }
        UpdateOverlay();
        if (_overlay is null) return;
        _editOverlay = !_editOverlay;
        _overlay.SetEditMode(_editOverlay);
        UpdateAdjustButton();
        if (!_editOverlay) SaveBounds();
    }

    private void UpdateOverlay()
    {
        if (_disposed) return;
        if (OverlayVisibleCheckBox.IsChecked != true || !HasMemoContent())
        {
            _editOverlay = false;
            _overlay?.SetEditMode(false);
            _overlay?.Hide();
            UpdateAdjustButton();
            return;
        }

        if (_overlay is null)
        {
            _overlay = new MemoOverlayWindow(_state.Left, _state.Top);
            _overlay.BoundsCommitted += (_, _) => SaveBounds();
        }
        _overlay.UpdateContent(MemoTextBox.Text, _items);
        _overlay.SetEditMode(_editOverlay);
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.Topmost = true;
        UpdateAdjustButton();
    }

    private void UpdateAdjustButton()
    {
        AdjustOverlayButton.Content = _editOverlay ? "完成调整" : "调整位置";
        AdjustOverlayButton.IsEnabled = OverlayVisibleCheckBox.IsChecked == true && HasMemoContent();
    }

    private bool HasMemoContent() => !string.IsNullOrWhiteSpace(MemoTextBox.Text) || _items.Count > 0;

    private void SaveBounds()
    {
        if (_overlay is not null)
            _state = _state with { Left = _overlay.Left, Top = _overlay.Top };
        SaveState();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveState();
    }

    private void SaveState()
    {
        _state = new MemoState(
            1,
            MemoTextBox?.Text ?? _state.Text,
            _items.ToArray(),
            OverlayVisibleCheckBox?.IsChecked == true,
            _state.Left,
            _state.Top).Normalize();
        if (SaveStatusText is not null) SaveStatusText.Text = _store.Save(_state) ? "已自动保存" : "保存失败";
    }

    private static bool Matches(FeatureMarketItem item, string query) =>
        new[] { item.NameZh, item.ShortNameZh, item.Name, item.ShortName, item.Id }
            .Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);

    private static bool StartsWith(FeatureMarketItem item, string query) =>
        new[] { item.NameZh, item.ShortNameZh, item.Name, item.ShortName }
            .Any(value => value?.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) == true);

    private static string DisplayName(FeatureMarketItem item) =>
        FirstNotEmpty(item.NameZh, item.ShortNameZh, item.Name, item.ShortName, item.Id);

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "未知物品";

    public sealed record MemoSearchResult(string Id, string Name, string ShortName, string IconLink);
}
