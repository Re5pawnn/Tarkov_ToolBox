using Microsoft.UI.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using TarkovMapLocator.App.Models;
using Windows.Foundation;
using Windows.Graphics;

namespace TarkovMapLocator.App.Pip;

public sealed partial class NativeMapPipWindow : Window
{
    private const double MarkerYawVisualOffsetDegrees = 180;
    private const double MinimumMapAspectRatio = 0.25;
    private const double MaximumMapAspectRatio = 4.0;
    private const int MinimumPipWidth = 220;
    private const int MinimumPipHeight = 220;
    private const int MaximumPipWidth = 1200;
    private const int MaximumPipHeight = 1200;
    private const string WebMapIconBaseUrl = "https://cdn.kaedeori.com/uploads/tarkov/map-icons";

    private readonly Dictionary<string, ImageSource> imageSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> markerIconSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<NativeMapPipPreferences> savePreferences;
    private NativeMapPipSnapshot? snapshot;
    private double mapWidth;
    private double mapHeight;
    private double fitScale = 1;
    private double zoom = 1;
    private double translateX;
    private double translateY;
    private bool isDraggingMap;
    private bool isDraggingWindow;
    private uint? dragPointerId;
    private Point lastPointerPosition;
    private NativePoint lastCursorPosition;

    public NativeMapPipWindow(
        NativeMapPipPreferences preferences,
        Action<NativeMapPipPreferences> savePreferences)
    {
        this.savePreferences = savePreferences;
        InitializeComponent();
        Title = "地图画中画";
        Root.Opacity = preferences.Opacity;
        zoom = preferences.Zoom;
        SetWindowPlacement(preferences);
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;
    }

    public event EventHandler? ClosedByUser;

    public NativeMapPipPreferences CurrentPreferences => new(
        true,
        Root.Opacity,
        zoom,
        AppWindow.Position.X,
        AppWindow.Position.Y,
        AppWindow.Size.Width,
        AppWindow.Size.Height);

    public void ApplySnapshot(NativeMapPipSnapshot nextSnapshot)
    {
        snapshot = nextSnapshot;
        Title = $"地图画中画 - {nextSnapshot.Map.Name}";

        if (!string.IsNullOrWhiteSpace(nextSnapshot.Map.ImagePath) && File.Exists(nextSnapshot.Map.ImagePath))
        {
            MapImage.Source = CreateImageSource(nextSnapshot.Map.ImagePath);
        }
        else
        {
            MapImage.Source = null;
        }

        RebuildMarkers();
        FitMap(resetView: false);
    }

    private void SetWindowPlacement(NativeMapPipPreferences preferences)
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(false, false);
        presenter!.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        var width = Math.Clamp(preferences.Width, MinimumPipWidth, MaximumPipWidth);
        var height = Math.Clamp(preferences.Height, MinimumPipHeight, MaximumPipHeight);
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(preferences.Left, preferences.Top));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppWindow.Changed -= OnAppWindowChanged;
        savePreferences(CurrentPreferences with { IsEnabled = false });
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (isDraggingWindow)
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            savePreferences(CurrentPreferences);
        }
    }

    private ImageSource CreateImageSource(string imagePath)
    {
        var modifiedUtcTicks = File.Exists(imagePath) ? File.GetLastWriteTimeUtc(imagePath).Ticks : 0;
        var cacheKey = $"{imagePath}|{modifiedUtcTicks}";
        if (imageSourceCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        ImageSource source = string.Equals(System.IO.Path.GetExtension(imagePath), ".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImageSource(new Uri(imagePath))
            : new BitmapImage(new Uri(imagePath));
        imageSourceCache[cacheKey] = source;
        return source;
    }

    private void FitMap(bool resetView)
    {
        if (snapshot is null || Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            return;
        }

        var aspectRatio = snapshot.Map.AspectRatio;
        if (!double.IsFinite(aspectRatio) || aspectRatio is < MinimumMapAspectRatio or > MaximumMapAspectRatio)
        {
            aspectRatio = 16.0 / 9.0;
        }

        var availableWidth = Math.Max(1, Root.ActualWidth);
        var availableHeight = Math.Max(1, Root.ActualHeight);
        if (availableWidth / availableHeight > aspectRatio)
        {
            mapHeight = availableHeight;
            mapWidth = mapHeight * aspectRatio;
        }
        else
        {
            mapWidth = availableWidth;
            mapHeight = mapWidth / aspectRatio;
        }

        MapSurface.Width = mapWidth;
        MapSurface.Height = mapHeight;
        MapImage.Width = mapWidth;
        MapImage.Height = mapHeight;
        MarkerLayer.Width = mapWidth;
        MarkerLayer.Height = mapHeight;
        fitScale = 1;
        if (resetView)
        {
            zoom = fitScale;
            translateX = (availableWidth - mapWidth) / 2.0;
            translateY = (availableHeight - mapHeight) / 2.0;
        }
        else
        {
            zoom = Math.Clamp(zoom, fitScale, 5);
            if (translateX == 0 && translateY == 0)
            {
                translateX = (availableWidth - mapWidth) / 2.0;
                translateY = (availableHeight - mapHeight) / 2.0;
            }
            ClampTranslation();
        }

        ApplyTransform();
    }

    private void SetZoom(double nextZoom, Point origin)
    {
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(nextZoom, fitScale, 5.0);
        var previousZoom = zoom;
        if (Math.Abs(previousZoom - clamped) < 0.001)
        {
            return;
        }

        var mapX = (origin.X - translateX) / previousZoom;
        var mapY = (origin.Y - translateY) / previousZoom;
        zoom = clamped;
        translateX = origin.X - mapX * zoom;
        translateY = origin.Y - mapY * zoom;
        ClampTranslation();
        ApplyTransform();
        savePreferences(CurrentPreferences);
    }

    private void ApplyTransform()
    {
        MapTransform.ScaleX = zoom;
        MapTransform.ScaleY = zoom;
        MapTransform.TranslateX = translateX;
        MapTransform.TranslateY = translateY;
        UpdateMarkerPositions();
    }

    private void ClampTranslation()
    {
        var availableWidth = Math.Max(1, Root.ActualWidth);
        var availableHeight = Math.Max(1, Root.ActualHeight);
        var scaledWidth = mapWidth * zoom;
        var scaledHeight = mapHeight * zoom;

        translateX = scaledWidth <= availableWidth
            ? (availableWidth - scaledWidth) / 2.0
            : Math.Clamp(translateX, availableWidth - scaledWidth, 0);
        translateY = scaledHeight <= availableHeight
            ? (availableHeight - scaledHeight) / 2.0
            : Math.Clamp(translateY, availableHeight - scaledHeight, 0);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitMap(resetView: false);
        savePreferences(CurrentPreferences);
    }

    private void RebuildMarkers()
    {
        MarkerLayer.Children.Clear();
        if (snapshot is null)
        {
            return;
        }

        foreach (var point in snapshot.Map.Points.Where(IsPipPoiPoint))
        {
            var marker = CreatePoiMarker(point);
            marker.Tag = point;
            MarkerLayer.Children.Add(marker);
        }

        if (snapshot.PlayerPoint is not null)
        {
            var marker = CreatePlayerMarker(snapshot.PlayerPoint);
            marker.Tag = snapshot.PlayerPoint;
            MarkerLayer.Children.Add(marker);
        }

        UpdateMarkerPositions();
    }

    private void UpdateMarkerPositions()
    {
        foreach (var child in MarkerLayer.Children)
        {
            if (child is not FrameworkElement element || element.Tag is not MapPrototypePoint point)
            {
                continue;
            }

            Canvas.SetLeft(element, point.U * mapWidth - element.Width / 2.0);
            Canvas.SetTop(element, point.V * mapHeight - element.Height / 2.0);
        }
    }

    private static bool IsPipPoiPoint(MapPrototypePoint point)
    {
        return string.Equals(point.Kind, "extract", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(point.Kind, "transits", StringComparison.OrdinalIgnoreCase);
    }

    private FrameworkElement CreatePoiMarker(MapPrototypePoint point)
    {
        var iconName = GetPoiIconName(point);
        if (!string.IsNullOrWhiteSpace(iconName))
        {
            return CreateWebMapIconMarker(point, ResolveMapIconUri(iconName), 26);
        }

        var grid = new Grid
        {
            Width = 26,
            Height = 26
        };

        grid.Children.Add(new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = new SolidColorBrush(Colors.DodgerBlue),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        return grid;
    }

    private FrameworkElement CreateWebMapIconMarker(MapPrototypePoint point, Uri iconUri, double size)
    {
        var canvas = new Canvas
        {
            Width = size,
            Height = size
        };

        canvas.Children.Add(new Image
        {
            Width = size,
            Height = size,
            Source = GetMarkerIconSource(iconUri),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (point.ShowLabel)
        {
            canvas.Children.Add(CreatePoiLabel(point, size));
        }

        return canvas;
    }

    private ImageSource GetMarkerIconSource(Uri iconUri)
    {
        var key = iconUri.AbsoluteUri;
        if (markerIconSourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = new BitmapImage(iconUri);
        markerIconSourceCache[key] = source;
        return source;
    }

    private static Uri ResolveMapIconUri(string iconName)
    {
        var localPath = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "map-icons", $"{iconName}.png");
        return System.IO.File.Exists(localPath)
            ? new Uri(localPath)
            : new Uri($"{WebMapIconBaseUrl}/{iconName}.png");
    }

    private static string GetPoiIconName(MapPrototypePoint point)
    {
        if (!string.IsNullOrWhiteSpace(point.IconName))
        {
            return point.IconName;
        }

        if (string.Equals(point.Kind, "extract", StringComparison.OrdinalIgnoreCase))
        {
            return "extract_shared";
        }

        return string.Equals(point.Kind, "transits", StringComparison.OrdinalIgnoreCase) ? "extract_transit" : "";
    }

    private static FrameworkElement CreatePoiLabel(MapPrototypePoint point, double iconSize)
    {
        var text = string.IsNullOrWhiteSpace(point.LabelText) ? point.Label : point.LabelText;
        var label = new Border
        {
            Padding = new Thickness(5, 2, 5, 3),
            Background = new SolidColorBrush(Colors.Black) { Opacity = 0.68 },
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            }
        };

        var left = iconSize * 0.72;
        if (point.U > 0.92)
        {
            left = -180;
            label.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else if (point.U < 0.08)
        {
            left = iconSize * 0.72;
        }

        var top = iconSize * 0.6;
        if (point.V > 0.9)
        {
            top = -24;
        }

        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        return label;
    }

    private static FrameworkElement CreatePlayerMarker(MapPrototypePoint point)
    {
        var grid = new Grid
        {
            Width = 34,
            Height = 44
        };

        grid.Children.Add(new Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = new SolidColorBrush(Colors.Orange),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        grid.Children.Add(new Polygon
        {
            Points =
            [
                new Point(17, 0),
                new Point(24, 14),
                new Point(10, 14)
            ],
            Fill = new SolidColorBrush(Colors.OrangeRed),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = ToMarkerRotation(point.YawDegrees) }
        });

        return grid;
    }

    private static double ToMarkerRotation(double yawDegrees)
    {
        return (yawDegrees + MarkerYawVisualOffsetDegrees) % 360;
    }

    private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        var zoomFactor = Math.Exp(point.Properties.MouseWheelDelta * 0.0012);
        SetZoom(zoom * zoomFactor, point.Position);
        e.Handled = true;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        isDraggingMap = true;
        isDraggingWindow = false;
        dragPointerId = point.PointerId;
        lastPointerPosition = point.Position;
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!isDraggingMap || dragPointerId != point.PointerId)
        {
            return;
        }

        translateX += point.Position.X - lastPointerPosition.X;
        translateY += point.Position.Y - lastPointerPosition.Y;
        ClampTranslation();
        ApplyTransform();
        lastPointerPosition = point.Position;
        e.Handled = true;
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragPointerId != e.GetCurrentPoint(Root).PointerId)
        {
            return;
        }

        isDraggingMap = false;
        isDraggingWindow = false;
        dragPointerId = null;
        Root.ReleasePointerCapture(e.Pointer);
        savePreferences(CurrentPreferences);
        e.Handled = true;
    }

    private void OnDragBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        isDraggingMap = false;
        isDraggingWindow = true;
        dragPointerId = point.PointerId;
        lastPointerPosition = point.Position;
        GetCursorPos(out lastCursorPosition);
        DragBar.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragBarPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!isDraggingWindow || dragPointerId != point.PointerId)
        {
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDragBarMove(e.Pointer);
            return;
        }

        if (!GetCursorPos(out var cursorPosition))
        {
            return;
        }

        var dx = cursorPosition.X - lastCursorPosition.X;
        var dy = cursorPosition.Y - lastCursorPosition.Y;
        var position = AppWindow.Position;
        AppWindow.Move(new PointInt32(
            position.X + dx,
            position.Y + dy));
        lastCursorPosition = cursorPosition;
        lastPointerPosition = point.Position;
        e.Handled = true;
    }

    private void OnDragBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDragBarMove(e.Pointer);
        e.Handled = true;
    }

    private void EndDragBarMove(Pointer pointer)
    {
        if (isDraggingWindow)
        {
            savePreferences(CurrentPreferences);
        }

        isDraggingWindow = false;
        dragPointerId = null;
        DragBar.ReleasePointerCapture(pointer);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
}
