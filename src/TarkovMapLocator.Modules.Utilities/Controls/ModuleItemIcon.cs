using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Utilities.Controls;

public sealed class ModuleItemIcon : Border
{
    public static readonly DependencyProperty FeatureHostProperty = DependencyProperty.RegisterAttached(
        "FeatureHost",
        typeof(IFeatureHost),
        typeof(ModuleItemIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(
        nameof(ItemId), typeof(string), typeof(ModuleItemIcon), new PropertyMetadata("", OnInputChanged));

    public static readonly DependencyProperty IconLinkProperty = DependencyProperty.Register(
        nameof(IconLink), typeof(string), typeof(ModuleItemIcon), new PropertyMetadata("", OnInputChanged));

    private readonly Image _image;
    private readonly TextBlock _placeholder;
    private int _requestVersion;
    private bool _loadQueued;

    public ModuleItemIcon()
    {
        Width = 42;
        Height = 42;
        Padding = new Thickness(3);
        Background = new SolidColorBrush(Color.FromRgb(17, 23, 21));
        BorderBrush = new SolidColorBrush(Color.FromRgb(43, 59, 52));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        ClipToBounds = true;

        _placeholder = new TextBlock
        {
            Text = "◈",
            Foreground = new SolidColorBrush(Color.FromRgb(105, 124, 114)),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _image = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        var surface = new Grid();
        surface.Children.Add(_placeholder);
        surface.Children.Add(_image);
        Child = surface;

        Loaded += (_, _) => QueueLoad();
        Unloaded += (_, _) => _requestVersion++;
    }

    public string ItemId
    {
        get => (string)GetValue(ItemIdProperty);
        set => SetValue(ItemIdProperty, value);
    }

    public string IconLink
    {
        get => (string)GetValue(IconLinkProperty);
        set => SetValue(IconLinkProperty, value);
    }

    public static void SetFeatureHost(DependencyObject element, IFeatureHost value) =>
        element.SetValue(FeatureHostProperty, value);

    public static IFeatureHost? GetFeatureHost(DependencyObject element) =>
        (IFeatureHost?)element.GetValue(FeatureHostProperty);

    private static void OnInputChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is ModuleItemIcon icon) icon.QueueLoad();
    }

    private void QueueLoad()
    {
        if (!IsLoaded || _loadQueued) return;
        _loadQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _loadQueued = false;
            if (IsLoaded) LoadAsync();
        }, DispatcherPriority.DataBind);
    }

    private async void LoadAsync()
    {
        var version = ++_requestVersion;
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
        var host = GetFeatureHost(this);
        if (host is null || string.IsNullOrWhiteSpace(ItemId)) return;

        try
        {
            var source = await host.LoadItemIconAsync(ItemId, IconLink);
            if (version != _requestVersion || !IsLoaded || source is null) return;
            _image.Source = source;
            _image.Visibility = Visibility.Visible;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Item artwork is decorative; keep the placeholder on transient failures.
        }
    }
}
