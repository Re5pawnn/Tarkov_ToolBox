using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.Utilities.Controls;

namespace TarkovMapLocator.Modules.Utilities.StoryGuide;

public partial class StoryGuideView : UserControl
{
    private readonly IFeatureHost _host;
    private readonly Action _navigateBack;
    private readonly StoryGuidePreferencesService _preferences;
    private string _gameMode;
    private readonly Dictionary<string, string> _selectedVariants = new(StringComparer.Ordinal);
    private bool _updatingVariantSelector;
    private int _loadVersion;

    public StoryGuideView(IFeatureHost host, Action navigateBack)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _navigateBack = navigateBack ?? throw new ArgumentNullException(nameof(navigateBack));
        _preferences = new StoryGuidePreferencesService(host);
        _gameMode = _preferences.Load();
        InitializeComponent();
        ModeSelector.SelectedValue = _gameMode;
        ModuleItemIcon.SetFeatureHost(this, host);
        ChapterList.ItemsSource = StoryGuideService.LoadCatalog();
        ChapterList.SelectedIndex = 0;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _navigateBack();

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeSelector.SelectedValue is not string mode || mode == _gameMode) return;
        _gameMode = mode;
        _preferences.Save(mode);
        _ = LoadSelectedAsync(forceRefresh: false);
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterList.SelectedItem is not StoryGuideChapter chapter) return;
        UpdateVariantSelector(chapter);
        _ = LoadSelectedAsync(forceRefresh: false);
    }

    private void VariantSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingVariantSelector || ChapterList.SelectedItem is not StoryGuideChapter chapter ||
            VariantSelector.SelectedItem is not StoryGuideVariant variant)
            return;
        _selectedVariants[chapter.Id] = variant.Id;
        _ = LoadSelectedAsync(forceRefresh: false);
    }

    private void UpdateVariantSelector(StoryGuideChapter chapter)
    {
        var variants = StoryGuideService.LoadVariants(chapter);
        _updatingVariantSelector = true;
        try
        {
            VariantSelector.ItemsSource = variants;
            var selectedId = _selectedVariants.GetValueOrDefault(chapter.Id);
            VariantSelector.SelectedItem = variants.FirstOrDefault(variant => variant.Id == selectedId) ?? variants.FirstOrDefault();
            VariantSelector.Visibility = variants.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingVariantSelector = false;
        }
    }

    private StoryGuideVariant? SelectedVariant(StoryGuideChapter chapter) =>
        VariantSelector.SelectedItem as StoryGuideVariant ?? StoryGuideService.LoadVariants(chapter).FirstOrDefault();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadSelectedAsync(forceRefresh: true);

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is not StoryGuideChapter chapter || SelectedVariant(chapter) is not { } variant) return;
        try
        {
            Process.Start(new ProcessStartInfo(variant.SourceUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "剧情攻略", "打开来源页面失败", exception.Message);
            _host.ShowNotification("无法打开剧情攻略来源页面");
        }
    }

    private void ImagePreview_Click(object? sender, EventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StoryGuideImage image }) return;
        var window = new ModuleImageWindow(_host, image.Title, image.ImageCacheId, image.ImageUrl)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private async Task LoadSelectedAsync(bool forceRefresh)
    {
        if (ChapterList.SelectedItem is not StoryGuideChapter chapter || SelectedVariant(chapter) is not { } variant) return;
        var version = ++_loadVersion;
        var gameMode = _gameMode;
        TitleText.Text = chapter.Name;
        EnglishNameText.Text = $"{chapter.EnglishName} · {chapter.Category}";
        SummaryText.Text = chapter.Summary;
        if (!forceRefresh)
        {
            RequirementsList.ItemsSource = null;
            RequirementsCard.Visibility = Visibility.Collapsed;
            StepsList.ItemsSource = null;
        }
        StatusText.Text = forceRefresh ? "正在刷新" : "正在加载";
        StatusText.Visibility = Visibility.Visible;

        var result = await StoryGuideService.GetAsync(chapter, variant, gameMode, forceRefresh);
        if (version != _loadVersion || ChapterList.SelectedItem is not StoryGuideChapter selected ||
            selected.Id != chapter.Id || SelectedVariant(selected)?.Id != variant.Id)
            return;
        if (result.Detail is not { } detail)
        {
            StatusText.Text = result.ErrorMessage ?? "剧情攻略加载失败";
            return;
        }

        RequirementsList.ItemsSource = detail.Requirements;
        RequirementsCard.Visibility = detail.Requirements.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StepsList.ItemsSource = detail.Steps;
        StatusText.Text = detail.Steps.Count > 0 ? "" : "攻略内容待补充";
        StatusText.Visibility = detail.Steps.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (result.UsedCachedFallback)
        {
            _host.ShowNotification(result.ErrorMessage ?? "剧情攻略刷新失败，已保留原内容");
            _host.WriteLog(FeatureLogLevel.Warning, "剧情攻略", "刷新失败，已保留原内容", $"章节: {chapter.Name}\n路线: {variant.Name}");
        }
        else
        {
            _host.WriteLog(FeatureLogLevel.Info, "剧情攻略", forceRefresh ? "章节已刷新" : "章节已加载", $"章节: {chapter.Name}\n路线: {variant.Name}\n模式: {gameMode}\n步骤: {detail.Steps.Count}");
        }
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width < 860);

    private void ApplyResponsiveLayout(bool compact)
    {
        if (compact)
        {
            ChapterColumn.Width = new GridLength(1, GridUnitType.Star);
            ColumnGap.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
            ChapterRow.Height = new GridLength(190);
            RowGap.Height = new GridLength(12);
            DetailRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ChapterPane, 0);
            Grid.SetRow(ChapterPane, 0);
            Grid.SetRowSpan(ChapterPane, 1);
            Grid.SetColumn(DetailPane, 0);
            Grid.SetRow(DetailPane, 2);
            Grid.SetRowSpan(DetailPane, 1);
            return;
        }

        ChapterColumn.Width = new GridLength(278);
        ColumnGap.Width = new GridLength(12);
        DetailColumn.Width = new GridLength(1, GridUnitType.Star);
        ChapterRow.Height = new GridLength(1, GridUnitType.Star);
        RowGap.Height = new GridLength(0);
        DetailRow.Height = new GridLength(0);
        Grid.SetColumn(ChapterPane, 0);
        Grid.SetRow(ChapterPane, 0);
        Grid.SetRowSpan(ChapterPane, 3);
        Grid.SetColumn(DetailPane, 2);
        Grid.SetRow(DetailPane, 0);
        Grid.SetRowSpan(DetailPane, 3);
    }
}
