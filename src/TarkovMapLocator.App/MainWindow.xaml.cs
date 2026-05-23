using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using TarkovMapLocator.App.Services;
using TarkovMapLocator.App.Views;

namespace TarkovMapLocator.App;

public sealed partial class MainWindow : Window
{
    private readonly LocalPathConfigService localPathConfigService = new();
    private readonly PythonBackendProcessService backendProcessService = new();
    private readonly NativeFolderPickerService folderPickerService;

    public MainWindow()
    {
        InitializeComponent();

        folderPickerService = new NativeFolderPickerService(this, localPathConfigService);

        Title = "TarkovMapLocator";
        SetInitialSize();

        RootFrame.NavigationFailed += OnNavigationFailed;
        Closed += OnClosed;
        RootFrame.Navigate(
            typeof(MainPage),
            new MainPageServices(
                backendProcessService,
                folderPickerService,
                localPathConfigService));
    }

    private void SetInitialSize()
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.Maximize();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.ShutdownNativeMapFeaturesAsync().GetAwaiter().GetResult();
        }

        backendProcessService.Dispose();
    }

    private static void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load page {e.SourcePageType.FullName}.");
    }
}

public sealed record MainPageServices(
    PythonBackendProcessService BackendProcessService,
    NativeFolderPickerService FolderPickerService,
    LocalPathConfigService LocalPathConfigService);
