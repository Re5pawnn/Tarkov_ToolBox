using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.Utilities.CultistCircle;
using TarkovMapLocator.Modules.Utilities.HideoutProfit;
using TarkovMapLocator.Modules.Utilities.SeasonDocuments;
using TarkovMapLocator.Modules.Utilities.StoryGuide;

namespace TarkovMapLocator.Modules.Utilities;

public partial class UtilitiesShellView : UserControl, IFeatureNavigationHandler
{
    private readonly IFeatureHost _host;
    private readonly Dictionary<string, FrameworkElement> _views = new(StringComparer.Ordinal);
    private readonly Stack<string> _backHistory = [];
    private readonly Stack<string> _forwardHistory = [];
    private string _activeRoute = FeatureRoutes.Utilities;

    public UtilitiesShellView(IFeatureHost host, string initialRoute)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        Show(IsKnownRoute(initialRoute) ? initialRoute : FeatureRoutes.Utilities, recordHistory: false, forward: true);
    }

    public bool NavigateBack()
    {
        if (!_backHistory.TryPop(out var route)) return false;
        _forwardHistory.Push(_activeRoute);
        Show(route, recordHistory: false, forward: false);
        return true;
    }

    public bool NavigateForward()
    {
        if (!_forwardHistory.TryPop(out var route)) return false;
        _backHistory.Push(_activeRoute);
        Show(route, recordHistory: false, forward: true);
        return true;
    }

    private void NavigateTo(string route) => Show(route, recordHistory: true, forward: true);

    private void Show(string route, bool recordHistory, bool forward)
    {
        if (!IsKnownRoute(route) || string.Equals(route, _activeRoute, StringComparison.Ordinal) && PageHost.Content is not null) return;
        if (recordHistory)
        {
            _backHistory.Push(_activeRoute);
            _forwardHistory.Clear();
        }

        _activeRoute = route;
        var view = GetOrCreateView(route);
        PageHost.Content = view;
        if (!SystemParameters.ClientAreaAnimation) return;
        view.Opacity = 0;
        var transform = new TranslateTransform(forward ? 12 : -12, 0);
        view.RenderTransform = transform;
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(transform.X, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private FrameworkElement GetOrCreateView(string route)
    {
        if (_views.TryGetValue(route, out var view)) return view;
        view = route switch
        {
            FeatureRoutes.Utilities => CreateHub(),
            UtilitiesRoutes.CultistCircle => new CultistCircleView(_host, NavigateBackToHub),
            UtilitiesRoutes.SeasonDocuments => new SeasonDocumentsView(_host, NavigateBackToHub),
            UtilitiesRoutes.HideoutProfit => new HideoutProfitView(_host, NavigateBackToHub),
            UtilitiesRoutes.StoryGuide => new StoryGuideView(_host, NavigateBackToHub),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "小工具组件不支持该页面路由。")
        };
        _views.Add(route, view);
        return view;
    }

    private UtilitiesView CreateHub()
    {
        var view = new UtilitiesView();
        view.NavigationRequested += (_, route) => NavigateTo(route);
        return view;
    }

    private void NavigateBackToHub()
    {
        if (!NavigateBack()) NavigateTo(FeatureRoutes.Utilities);
    }

    private static bool IsKnownRoute(string route) => route is
        FeatureRoutes.Utilities or
        UtilitiesRoutes.CultistCircle or
        UtilitiesRoutes.SeasonDocuments or
        UtilitiesRoutes.HideoutProfit or
        UtilitiesRoutes.StoryGuide;
}
