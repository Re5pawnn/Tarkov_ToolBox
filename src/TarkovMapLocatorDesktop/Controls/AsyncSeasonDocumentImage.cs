using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovMapLocatorDesktop.Services;

namespace TarkovMapLocatorDesktop.Controls;

public sealed class AsyncSeasonDocumentImage : Border
{
    public static readonly DependencyProperty ImageIdProperty = DependencyProperty.Register(
        nameof(ImageId),
        typeof(string),
        typeof(AsyncSeasonDocumentImage),
        new PropertyMetadata("", OnImageInputChanged));

    public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
        nameof(ImageUrl),
        typeof(string),
        typeof(AsyncSeasonDocumentImage),
        new PropertyMetadata("", OnImageInputChanged));

    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly Canvas _zoomCursorLayer;
    private readonly Border _zoomCursor;
    private int _requestVersion;
    private bool _loadQueued;

    public AsyncSeasonDocumentImage()
    {
        Width = 310;
        Height = 174;
        Margin = new Thickness(0, 9, 0, 0);
        Background = new SolidColorBrush(Color.FromRgb(8, 12, 11));
        BorderBrush = new SolidColorBrush(Color.FromRgb(45, 62, 55));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        ClipToBounds = true;

        _image = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        _status = new TextBlock
        {
            Text = "位置截图加载中…",
            Foreground = new SolidColorBrush(Color.FromRgb(132, 151, 141)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid();
        grid.Children.Add(_status);
        grid.Children.Add(_image);

        var magnifier = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 10,3 A 7,7 0 1 1 10,17 A 7,7 0 1 1 10,3 M 15,15 L 22,22"),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _zoomCursor = new Border
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(5),
            Background = new SolidColorBrush(Color.FromArgb(218, 8, 12, 11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(132, 151, 141)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Child = new Viewbox { Child = magnifier }
        };
        _zoomCursorLayer = new Canvas
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _zoomCursorLayer.Children.Add(_zoomCursor);
        grid.Children.Add(_zoomCursorLayer);
        Child = grid;

        Loaded += (_, _) => QueueLoad();
        Unloaded += (_, _) =>
        {
            _requestVersion++;
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            _status.Visibility = Visibility.Visible;
            HideZoomCursor();
        };
        MouseEnter += (_, eventArgs) => ShowZoomCursor(eventArgs.GetPosition(this));
        MouseMove += (_, eventArgs) => ShowZoomCursor(eventArgs.GetPosition(this));
        MouseLeave += (_, _) => HideZoomCursor();
        PreviewMouseLeftButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            if (_image.Source is null)
            {
                _status.Text = "位置截图加载中…";
                QueueLoad();
                return;
            }
            PreviewClicked?.Invoke(this, EventArgs.Empty);
        };
    }

    public AsyncSeasonDocumentImage(string imageId, string imageUrl) : this()
    {
        ImageId = imageId;
        ImageUrl = imageUrl;
    }

    public string ImageId
    {
        get => (string)GetValue(ImageIdProperty);
        set => SetValue(ImageIdProperty, value);
    }

    public string ImageUrl
    {
        get => (string)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public event EventHandler? PreviewClicked;

    private static void OnImageInputChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not AsyncSeasonDocumentImage preview) return;
        preview._requestVersion++;
        preview._image.Source = null;
        preview._image.Visibility = Visibility.Collapsed;
        preview._status.Text = "位置截图加载中…";
        preview._status.Visibility = Visibility.Visible;
        preview.HideZoomCursor();
        if (preview.IsLoaded) preview.QueueLoad();
    }

    private void QueueLoad()
    {
        if (_loadQueued || !IsLoaded || string.IsNullOrWhiteSpace(ImageId) || string.IsNullOrWhiteSpace(ImageUrl)) return;
        _loadQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _loadQueued = false;
            LoadAsync();
        }, DispatcherPriority.Background);
    }

    private async void LoadAsync()
    {
        var version = ++_requestVersion;
        var imageId = ImageId;
        var imageUrl = ImageUrl;
        _status.Text = "位置截图加载中…";
        _status.Visibility = Visibility.Visible;
        ImageSource? source = null;
        var retryDelays = new[] { 350, 1000 };
        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            source = await SeasonDocumentImageService.GetPreviewAsync(imageId, imageUrl);
            if (version != _requestVersion || !IsLoaded) return;
            if (source is not null) break;
            if (attempt >= retryDelays.Length) continue;
            _status.Text = "位置截图正在重试…";
            await Task.Delay(retryDelays[attempt]);
            if (version != _requestVersion || !IsLoaded) return;
        }

        if (source is null)
        {
            _status.Text = "位置截图加载失败，点击重试";
            return;
        }

        _image.Source = source;
        _image.Visibility = Visibility.Visible;
        _status.Visibility = Visibility.Collapsed;
        if (IsMouseOver) ShowZoomCursor(Mouse.GetPosition(this));
    }

    private void ShowZoomCursor(Point position)
    {
        if (_image.Source is null) return;

        const double size = 34;
        var left = Math.Clamp(position.X - (size / 2), 0, Math.Max(0, ActualWidth - size));
        var top = Math.Clamp(position.Y - (size / 2), 0, Math.Max(0, ActualHeight - size));
        Canvas.SetLeft(_zoomCursor, left);
        Canvas.SetTop(_zoomCursor, top);
        _zoomCursorLayer.Visibility = Visibility.Visible;
        Cursor = Cursors.None;
    }

    private void HideZoomCursor()
    {
        _zoomCursorLayer.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
    }
}
