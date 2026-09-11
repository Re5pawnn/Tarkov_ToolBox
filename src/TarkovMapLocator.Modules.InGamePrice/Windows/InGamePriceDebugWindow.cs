using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TarkovMapLocator.Modules.InGamePrice.Windows;

/// <summary>
/// A resizable, map-like viewport for PP-OCR diagnostics.
/// </summary>
public sealed class InGamePriceDebugWindow : Window
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 6;
    private const double ZoomStep = 1.16;

    private readonly Grid _viewport;
    private readonly Canvas _content;
    private readonly Image _image;
    private readonly TextBlock _statusText;
    private readonly TextBlock _shortcutText;
    private readonly TextBlock _zoomText;
    private readonly TextBlock _emptyText;
    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translateTransform = new();
    private bool _hasFrame;
    private bool _fitRequested = true;
    private bool _isPanning;
    private MouseButton _panButton;
    private Point _lastPanPoint;
    private int _frameWidth;
    private int _frameHeight;
    private double _zoom = 1;
    private double _panX;
    private double _panY;

    public InGamePriceDebugWindow()
    {
        Title = "游戏内查价 · 调试取景框";
        Width = 1160;
        Height = 760;
        MinWidth = 720;
        MinHeight = 500;
        // The window intentionally has no owner, so it can stay visible while
        // the main workspace is minimized.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = Brush("#101514");
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { Margin = new Thickness(2, 0, 2, 11) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel();
        headingText.Children.Add(new TextBlock
        {
            Text = "窗口化调试取景框",
            Foreground = Brushes.White,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });
        _statusText = new TextBlock
        {
            Text = "等待调试画面…",
            Foreground = Brush("#91A89D"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        headingText.Children.Add(_statusText);
        _shortcutText = new TextBlock
        {
            Foreground = Brush("#F4B53F"),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        headingText.Children.Add(_shortcutText);
        heading.Children.Add(headingText);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _zoomText = new TextBlock
        {
            Text = "画面 100%",
            Foreground = Brush("#F4B53F"),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        };
        var fitButton = new Button
        {
            Content = "适配窗口",
            Background = Brush("#17201D"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#465E52"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 5, 11, 5),
            FontSize = 11,
            Cursor = Cursors.Hand,
            ToolTip = "将调试画面缩放至适合当前窗口"
        };
        fitButton.Click += (_, _) => FitToViewport();
        actions.Children.Add(_zoomText);
        actions.Children.Add(fitButton);
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        root.Children.Add(heading);

        _viewport = new Grid
        {
            Background = Brush("#0B100E"),
            ClipToBounds = true,
            Cursor = Cursors.Arrow
        };
        _viewport.SizeChanged += (_, _) =>
        {
            if (_fitRequested) FitToViewport();
        };
        _viewport.MouseDown += Viewport_MouseDown;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseUp += Viewport_MouseUp;
        _viewport.MouseWheel += Viewport_MouseWheel;
        _viewport.LostMouseCapture += Viewport_LostMouseCapture;

        _content = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0, 0)
        };
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_translateTransform);
        _content.RenderTransform = transformGroup;

        _image = new Image
        {
            Stretch = Stretch.None,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        _content.Children.Add(_image);
        _viewport.Children.Add(_content);

        _emptyText = new TextBlock
        {
            Text = "开启调试框后，这里会显示本机抓取的游戏画面。\n滚轮缩放，左键或中键拖动画面。",
            Foreground = Brush("#83988E"),
            FontSize = 12,
            LineHeight = 19,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _viewport.Children.Add(_emptyText);

        var viewportBorder = new Border
        {
            BorderBrush = Brush("#314238"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = _viewport
        };
        Grid.SetRow(viewportBorder, 1);
        root.Children.Add(viewportBorder);

        var helpText = new TextBlock
        {
            Text = "滚轮缩放调试画面，左键或中键拖拽平移。调试图只在本机内存中生成，不会写入截图目录。",
            Foreground = Brush("#83988E"),
            FontSize = 10,
            LineHeight = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 9, 2, 0)
        };
        Grid.SetRow(helpText, 2);
        root.Children.Add(helpText);
        Content = root;
    }

    public void UpdateFrame(
        ImageSource? image,
        int width,
        int height,
        string? status,
        string? shortcutText)
    {
        _statusText.Text = string.IsNullOrWhiteSpace(status) ? "等待调试画面…" : status;
        _shortcutText.Text = shortcutText ?? string.Empty;
        var hasUsableFrame = image is not null && width > 0 && height > 0;
        if (!hasUsableFrame)
        {
            _hasFrame = false;
            _image.Source = null;
            _content.Width = 0;
            _content.Height = 0;
            _emptyText.Visibility = Visibility.Visible;
            return;
        }

        var dimensionsChanged = _frameWidth != width || _frameHeight != height;
        _hasFrame = true;
        _frameWidth = width;
        _frameHeight = height;
        _image.Source = image;
        _image.Width = width;
        _image.Height = height;
        _content.Width = width;
        _content.Height = height;
        _emptyText.Visibility = Visibility.Collapsed;
        if (dimensionsChanged) _fitRequested = true;
        if (_fitRequested) FitToViewport();
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_hasFrame) return;

        var viewportPoint = e.GetPosition(_viewport);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        BeginPanning(viewportPoint, e.ChangedButton);
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        var viewportPoint = e.GetPosition(_viewport);
        if (_isPanning)
        {
            _panX += viewportPoint.X - _lastPanPoint.X;
            _panY += viewportPoint.Y - _lastPanPoint.Y;
            _lastPanPoint = viewportPoint;
            ApplyViewportTransform();
            e.Handled = true;
            return;
        }
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && e.ChangedButton == _panButton)
        {
            EndPanning();
            e.Handled = true;
            return;
        }
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_hasFrame || e.Delta == 0) return;

        var steps = Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120);
        var nextZoom = Math.Clamp(_zoom * Math.Pow(ZoomStep, steps), MinZoom, MaxZoom);
        if (Math.Abs(nextZoom - _zoom) < 0.0001) return;

        var viewportPoint = e.GetPosition(_viewport);
        var imagePoint = ToImagePoint(viewportPoint);
        _zoom = nextZoom;
        _panX = viewportPoint.X - imagePoint.X * _zoom;
        _panY = viewportPoint.Y - imagePoint.Y * _zoom;
        _fitRequested = false;
        ApplyViewportTransform();
        e.Handled = true;
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            _viewport.Cursor = Cursors.Arrow;
        }
    }

    private void BeginPanning(Point viewportPoint, MouseButton button)
    {
        _isPanning = true;
        _panButton = button;
        _lastPanPoint = viewportPoint;
        _viewport.Cursor = Cursors.SizeAll;
        _viewport.CaptureMouse();
    }

    private void EndPanning()
    {
        _isPanning = false;
        _viewport.Cursor = Cursors.Arrow;
        if (_viewport.IsMouseCaptured) _viewport.ReleaseMouseCapture();
    }

    private void FitToViewport()
    {
        if (!_hasFrame || _frameWidth <= 0 || _frameHeight <= 0 ||
            _viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0)
        {
            _fitRequested = true;
            return;
        }

        _zoom = Math.Clamp(
            Math.Min(_viewport.ActualWidth / _frameWidth, _viewport.ActualHeight / _frameHeight),
            MinZoom,
            MaxZoom);
        _panX = (_viewport.ActualWidth - _frameWidth * _zoom) / 2;
        _panY = (_viewport.ActualHeight - _frameHeight * _zoom) / 2;
        _fitRequested = false;
        ApplyViewportTransform();
    }

    private Point ToImagePoint(Point viewportPoint) => new(
        (viewportPoint.X - _panX) / _zoom,
        (viewportPoint.Y - _panY) / _zoom);

    private void ApplyViewportTransform()
    {
        _scaleTransform.ScaleX = _zoom;
        _scaleTransform.ScaleY = _zoom;
        _translateTransform.X = _panX;
        _translateTransform.Y = _panY;
        _zoomText.Text = $"画面 {_zoom:P0}";
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
