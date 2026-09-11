using System.Windows;
using System.Windows.Controls;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.Utilities.Controls;

namespace TarkovMapLocator.Modules.Utilities.CultistCircle;

public partial class CultistCircleView : UserControl
{
    private readonly Action _navigateBack;

    public CultistCircleView(IFeatureHost host, Action navigateBack)
    {
        ArgumentNullException.ThrowIfNull(host);
        _navigateBack = navigateBack ?? throw new ArgumentNullException(nameof(navigateBack));
        InitializeComponent();
        ModuleItemIcon.SetFeatureHost(this, host);
        UpdateRecipes();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _navigateBack();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRecipes();

    private void UpdateRecipes()
    {
        try
        {
            var catalog = CultistCircleRecipeService.Load();
            var recipes = CultistCircleRecipeService.Search(SearchBox?.Text ?? "");
            ResultsList.ItemsSource = recipes;
            StatusText.Text = $"{recipes.Count:N0} / {catalog.Recipes.Count:N0} 条配方 · 数据 {catalog.UpdatedAt.LocalDateTime:yyyy-MM-dd}";
            EmptyText.Visibility = recipes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ResultsList.ItemsSource = null;
            StatusText.Text = $"读取邪教圈配方失败：{exception.Message}";
            EmptyText.Visibility = Visibility.Visible;
        }
    }
}
