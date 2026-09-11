using System.Windows;
using System.Windows.Controls;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.Utilities.Controls;

namespace TarkovMapLocator.Modules.Utilities.SeasonDocuments;

public partial class SeasonDocumentsView : UserControl
{
    private readonly IFeatureHost _host;
    private readonly Action _navigateBack;
    private bool _updatingFilters;

    public SeasonDocumentsView(IFeatureHost host, Action navigateBack)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _navigateBack = navigateBack ?? throw new ArgumentNullException(nameof(navigateBack));
        InitializeComponent();
        ModuleItemIcon.SetFeatureHost(this, host);
        UpdatePage();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _navigateBack();

    private void DocumentSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters) return;
        UpdateMapOptions();
        UpdateResults();
    }

    private void MapSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilters) UpdateResults();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateResults();

    private void Preview_Click(object? sender, EventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SeasonDocumentLocation location } ||
            string.IsNullOrWhiteSpace(location.ImageCacheId) ||
            string.IsNullOrWhiteSpace(location.ImageUrl))
            return;
        var window = new ModuleImageWindow(_host, location.Title, location.ImageCacheId, location.ImageUrl)
        {
            Owner = Window.GetWindow(this)
        };
        window.Show();
        window.Activate();
    }

    private void UpdatePage()
    {
        try
        {
            _updatingFilters = true;
            DocumentSelector.ItemsSource = SeasonDocumentLocationService.Load().Documents;
            DocumentSelector.SelectedIndex = 0;
            _updatingFilters = false;
            UpdateMapOptions();
            UpdateResults();
        }
        catch (Exception exception)
        {
            _updatingFilters = false;
            ResultsList.ItemsSource = null;
            StatusText.Text = $"读取赛季文档刷新位置失败：{exception.Message}";
            EmptyText.Visibility = Visibility.Visible;
            _host.WriteLog(FeatureLogLevel.Error, "小工具", "读取赛季文档刷新位置失败", exception.ToString());
        }
    }

    private void UpdateMapOptions()
    {
        if (DocumentSelector.SelectedItem is not SeasonDocumentDefinition document) return;
        var previous = MapSelector.SelectedItem as string;
        var options = new[] { "全部地图" }.Concat(document.Maps.Select(map => map.LocalizedName)).ToArray();
        _updatingFilters = true;
        MapSelector.ItemsSource = options;
        MapSelector.SelectedItem = previous is not null && options.Contains(previous) ? previous : options[0];
        _updatingFilters = false;
    }

    private void UpdateResults()
    {
        if (DocumentSelector.SelectedItem is not SeasonDocumentDefinition document) return;
        var selectedMap = MapSelector.SelectedItem as string ?? "全部地图";
        var locations = SeasonDocumentLocationService.Search(
            document.Id,
            selectedMap == "全部地图" ? "" : selectedMap,
            SearchBox?.Text ?? "");
        ResultsList.ItemsSource = locations;
        StatusText.Text = $"{locations.Count:N0} / {document.Locations.Count:N0} 处刷新点";
        EmptyText.Visibility = locations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
