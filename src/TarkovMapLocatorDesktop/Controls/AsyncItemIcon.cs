using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovMapLocatorDesktop.Services;

namespace TarkovMapLocatorDesktop.Controls;

/// <summary>
/// A virtualisation-safe item thumbnail. It only starts loading after its row has
/// been realised and ignores any late result when WPF recycles the container.
/// </summary>
public sealed class AsyncItemIcon : Border
{
    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(
        nameof(ItemId),
        typeof(string),
        typeof(AsyncItemIcon),
        new PropertyMetadata("", OnIconInputChanged));

    public static readonly DependencyProperty IconLinkProperty = DependencyProperty.Register(
        nameof(IconLink),
        typeof(string),
        typeof(AsyncItemIcon),
        new PropertyMetadata("", OnIconInputChanged));

    public static readonly DependencyProperty FallbackIconLinkProperty = DependencyProperty.Register(
        nameof(FallbackIconLink),
        typeof(string),
        typeof(AsyncItemIcon),
        new PropertyMetadata("", OnIconInputChanged));

    /// <summary>
    /// Keeps full-resolution item artwork separate from the compact market icon
    /// cache. A small list icon must not win for a workbench thumbnail of the
    /// same Tarkov item.
    /// </summary>
    public static readonly DependencyProperty PreferFullImageProperty = DependencyProperty.Register(
        nameof(PreferFullImage),
        typeof(bool),
        typeof(AsyncItemIcon),
        new PropertyMetadata(false, OnIconInputChanged));

    private readonly Image image;
    private readonly TextBlock placeholder;
    private int requestVersion;
    private bool loadQueued;

    public AsyncItemIcon()
    {
        Width = 42;
        Height = 42;
        Padding = new Thickness(3);
        Background = new SolidColorBrush(Color.FromRgb(17, 23, 21));
        BorderBrush = new SolidColorBrush(Color.FromRgb(43, 59, 52));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        placeholder = new TextBlock
        {
            Text = "◈",
            Foreground = new SolidColorBrush(Color.FromRgb(105, 124, 114)),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        image = new Image
        {
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed
        };

        var surface = new Grid();
        surface.Children.Add(placeholder);
        surface.Children.Add(image);
        Child = surface;

        Loaded += (_, _) => QueueIconLoad();
        Unloaded += (_, _) => requestVersion++;
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

    public string FallbackIconLink
    {
        get => (string)GetValue(FallbackIconLinkProperty);
        set => SetValue(FallbackIconLinkProperty, value);
    }

    public bool PreferFullImage
    {
        get => (bool)GetValue(PreferFullImageProperty);
        set => SetValue(PreferFullImageProperty, value);
    }

    private static void OnIconInputChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is AsyncItemIcon icon) icon.QueueIconLoad();
    }

    private void QueueIconLoad()
    {
        if (!IsLoaded || loadQueued) return;
        loadQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            loadQueued = false;
            if (IsLoaded) LoadIconAsync();
        }, DispatcherPriority.DataBind);
    }

    private async void LoadIconAsync()
    {
        var version = ++requestVersion;
        var itemId = ItemId;
        var iconLink = IconLink;
        var fallbackIconLink = FallbackIconLink;
        var preferFullImage = PreferFullImage;
        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        placeholder.Visibility = Visibility.Visible;

        // A cached icon remains usable even when the currently loaded market row has
        // no remote link (for example after a temporary API field omission).
        if (string.IsNullOrWhiteSpace(itemId)) return;

        try
        {
            var source = await ItemIconService.GetAsync(itemId, iconLink, fallbackIconLink, preferFullImage);
            if (version != requestVersion || !IsLoaded || source is null) return;

            image.Source = source;
            image.Visibility = Visibility.Visible;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // An icon is decorative. The placeholder remains available if a remote
            // image is malformed or temporarily unavailable.
        }
    }
}
