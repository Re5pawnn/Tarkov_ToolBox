using System.Windows;
using System.Windows.Controls;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Utilities;

public partial class UtilitiesView : UserControl
{
    public UtilitiesView()
    {
        InitializeComponent();
    }

    public event EventHandler<string>? NavigationRequested;

    private void FeatureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string route }) NavigationRequested?.Invoke(this, route);
    }
}
