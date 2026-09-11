using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TarkovMapLocatorDesktop.Controls;
using TarkovMapLocatorDesktop.Models;
using TarkovMapLocatorDesktop.Services;
using TarkovMapLocatorDesktop.Utilities;

namespace TarkovMapLocatorDesktop.Windows;

public partial class MiniMapWindow : Window
{
    private sealed record ExtractLabelLayout(Canvas Host, Border Label, Line Leader, MapMarker Marker, double DotSize);
    private sealed record RenderedMiniMarker(FrameworkElement Element, MapMarker Marker, double Size, ExtractLabelLayout? LabelLayout);

    private const double MinZoom = 0.75;
    private const double MaxZoom = 4;
    private const double ZoomStep = 1.16;
    private static readonly Geometry DirectionArrowGeometry = Geometry.Parse("M8,0 L16,8 L11.5,8 L11.5,16 L4.5,16 L4.5,8 L0,8 Z");
    private static readonly Geometry LockMarkerGeometry = Geometry.Parse("M5,7 V5 A3,3 0 0 1 11,5 V7 M3.5,7 H12.5 V14 H3.5 Z M8,9.5 V12");
    private IReadOnlyList<MapMarker> _staticMarkers = [];
    private IReadOnlyList<MapMarker> _dynamicMarkers = [];
    private readonly List<ExtractLabelLayout> _extractLabelLayouts = [];
    private readonly List<RenderedMiniMarker> _staticRenderedMarkers = [];
    private readonly List<RenderedMiniMarker> _dynamicRenderedMarkers = [];
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private Point _lastPointer;
    private string? _staticMarkerSignature;
    private string? _dynamicMarkerSignature;
    private bool _repositionQueued;
    private bool _arrangeLabelsAfterReposition;
    private AirdropMapOverlay? _airdropOverlay;
    private MapPointProjection _markerProjection = MapPointProjection.Identity;
    private string? _mapId;
    private string? _mapStyle;
    private int _layerTransitionVersion;
    private bool _isWindowLocked;

    public MiniMapWindow()
    {
        InitializeComponent();
        Topmost = true;
        ShowInTaskbar = false;
        ApplyWindowLockState();
    }

    public void UpdateMap(
        ImageSource? image,
        ImageSource? layerImage,
        string mapName,
        string? layerName,
        IEnumerable<MapMarker> markers,
        AirdropMapOverlay? airdropOverlay,
        MapPointProjection markerProjection,
        string? mapId,
        string? mapStyle)
    {
        var mapChanged = !ReferenceEquals(MiniMapImage.Source, image);
        var projectionChanged = _markerProjection != markerProjection ||
                                !string.Equals(_mapId, mapId, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(_mapStyle, mapStyle, StringComparison.OrdinalIgnoreCase);
        var layerChanged = !ReferenceEquals(MiniMapLayerImage.Source, layerImage);
        var nextMarkers = markers.Where(marker => marker.Type is "player" or "player-stale" or "extract" or "transit" or "task" or "key-room" or "switch" or "season-document" or "peer" or "btr" or "airdrop-a" or "airdrop-b" or "airdrop-estimate").ToArray();
        var nextStaticMarkers = nextMarkers.Where(marker => marker.Type is "extract" or "transit" or "task" or "key-room" or "switch" or "season-document").ToArray();
        var nextDynamicMarkers = nextMarkers.Where(marker => marker.Type is "player" or "player-stale" or "peer" or "btr" or "airdrop-a" or "airdrop-b" or "airdrop-estimate").ToArray();
        var staticSignature = BuildMarkerSignature(nextStaticMarkers);
        var dynamicSignature = BuildMarkerSignature(nextDynamicMarkers);
        MiniMapImage.Source = image;
        MapNameText.Text = string.IsNullOrWhiteSpace(layerName) ? mapName : $"{mapName} · {layerName}";
        if (layerChanged) ApplyLayerVisual(layerImage);
        _staticMarkers = nextStaticMarkers;
        _dynamicMarkers = nextDynamicMarkers;
        _airdropOverlay = airdropOverlay;
        _markerProjection = markerProjection;
        _mapId = mapId;
        _mapStyle = mapStyle;
        if (mapChanged) ResetView();
        var staticChanged = mapChanged || projectionChanged || !string.Equals(staticSignature, _staticMarkerSignature, StringComparison.Ordinal);
        var dynamicChanged = mapChanged || projectionChanged || !string.Equals(dynamicSignature, _dynamicMarkerSignature, StringComparison.Ordinal);
        if (!staticChanged && !dynamicChanged)
        {
            QueueReposition(arrangeLabels: true);
            return;
        }

        if (staticChanged)
        {
            _staticMarkerSignature = staticSignature;
            RedrawMarkerLayer(MiniMarkerCanvas, _staticRenderedMarkers, _staticMarkers);
        }
        if (dynamicChanged)
        {
            _dynamicMarkerSignature = dynamicSignature;
            RedrawMarkerLayer(MiniDynamicMarkerCanvas, _dynamicRenderedMarkers, _dynamicMarkers);
        }
        RepositionMarkers();
    }

    private void ApplyLayerVisual(ImageSource? layerImage)
    {
        var version = ++_layerTransitionVersion;
        MiniMapImage.BeginAnimation(OpacityProperty, null);
        MiniMapLayerImage.BeginAnimation(OpacityProperty, null);
        MiniMapLayerImage.Source = layerImage;

        if (layerImage is null)
        {
            MiniMapLayerImage.Opacity = 0;
            MiniMapLayerImage.Visibility = Visibility.Collapsed;
            AnimateOpacity(MiniMapImage, .93, 170, version);
            return;
        }

        MiniMapLayerImage.Visibility = Visibility.Visible;
        MiniMapLayerImage.Opacity = 0;
        AnimateOpacity(MiniMapImage, .14, 170, version);
        AnimateOpacity(MiniMapLayerImage, .98, 190, version);
    }

    private void AnimateOpacity(UIElement element, double target, int milliseconds, int version)
    {
        var animation = new DoubleAnimation(element.Opacity, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (version != _layerTransitionVersion) return;
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = target;
        };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClampPan();
        ApplyMapTransform();
        QueueReposition(arrangeLabels: true);
    }

    private void MapHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var multiplier = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        var nextZoom = Math.Clamp(_zoom * multiplier, MinZoom, MaxZoom);
        if (Math.Abs(nextZoom - _zoom) < 0.001) return;

        var focus = e.GetPosition(MapHost);
        var centerX = MapHost.ActualWidth / 2d;
        var centerY = MapHost.ActualHeight / 2d;
        var ratio = nextZoom / _zoom;
        _panX = focus.X - centerX - (focus.X - centerX - _panX) * ratio;
        _panY = focus.Y - centerY - (focus.Y - centerY - _panY) * ratio;
        _zoom = nextZoom;
        ClampPan();
        ApplyMapTransform();
        QueueReposition(arrangeLabels: true);
        e.Handled = true;
    }

    private void MapHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _isPanning = true;
        _lastPointer = e.GetPosition(MapHost);
        MapHost.Cursor = Cursors.SizeAll;
        MapHost.CaptureMouse();
        e.Handled = true;
    }

    private void MapHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(MapHost);
        _panX += pointer.X - _lastPointer.X;
        _panY += pointer.Y - _lastPointer.Y;
        _lastPointer = pointer;
        ClampPan();
        ApplyMapTransform();
        QueueReposition(arrangeLabels: false);
        e.Handled = true;
    }

    private void MapHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
        e.Handled = true;
    }

    private void MapHost_LostMouseCapture(object sender, MouseEventArgs e) => EndPan(releaseCapture: false);

    private void EndPan(bool releaseCapture = true)
    {
        if (!_isPanning) return;
        _isPanning = false;
        MapHost.Cursor = Cursors.Arrow;
        if (releaseCapture && MapHost.IsMouseCaptured) MapHost.ReleaseMouseCapture();
        QueueReposition(arrangeLabels: true);
    }

    private void ResetView()
    {
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        ApplyMapTransform();
    }

    private void ApplyMapTransform()
    {
        if (MiniMapScaleTransform is null || MiniMapTranslateTransform is null) return;
        MiniMapScaleTransform.ScaleX = _zoom;
        MiniMapScaleTransform.ScaleY = _zoom;
        MiniMapTranslateTransform.X = _panX;
        MiniMapTranslateTransform.Y = _panY;
    }

    private void ClampPan()
    {
        if (MapHost is null || MapHost.ActualWidth <= 0 || MapHost.ActualHeight <= 0) return;
        var baseBounds = GetImageDisplayBounds();
        var maxX = Math.Max(0, (baseBounds.Width * _zoom - MapHost.ActualWidth) / 2d);
        var maxY = Math.Max(0, (baseBounds.Height * _zoom - MapHost.ActualHeight) / 2d);
        _panX = Math.Clamp(_panX, -maxX, maxX);
        _panY = Math.Clamp(_panY, -maxY, maxY);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MiniMapDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        e.Handled = true;
        if (_isWindowLocked) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can reject a late mouse transition while the window is closing.
        }
    }

    private void MiniMapLock_Click(object sender, RoutedEventArgs e)
    {
        _isWindowLocked = !_isWindowLocked;
        ApplyWindowLockState();
    }

    private void ApplyWindowLockState()
    {
        ResizeMode = _isWindowLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
        MiniMapDragHandle.Cursor = _isWindowLocked ? Cursors.Arrow : Cursors.SizeAll;
        MiniMapDragHandle.ToolTip = _isWindowLocked ? "悬浮地图已锁定" : "拖动悬浮地图";
        MiniMapLockButton.Content = _isWindowLocked ? "解" : "锁";
        MiniMapLockButton.ToolTip = _isWindowLocked ? "解锁悬浮地图位置和尺寸" : "锁定悬浮地图位置和尺寸";
    }

    private void RedrawMarkerLayer(
        Canvas canvas,
        List<RenderedMiniMarker> renderedMarkers,
        IReadOnlyList<MapMarker> markers)
    {
        if (canvas is null || MapHost.ActualWidth < 1 || MapHost.ActualHeight < 1) return;
        canvas.Children.Clear();
        renderedMarkers.Clear();
        foreach (var marker in markers)
        {
            var isPlayer = marker.Type is "player" or "player-stale";
            var isKeyRoom = marker.Type == "key-room";
            var isSwitch = marker.Type == "switch";
            var isSeasonDocument = marker.Type == "season-document";
            var isBtr = marker.Type == "btr";
            var isAirdropBearing = marker.Type is "airdrop-a" or "airdrop-b";
            var isAirdropEstimate = marker.Type == "airdrop-estimate";
            var isLabeledPoi = marker.Type is "extract" or "transit" or "switch" or "season-document" or "btr" or "task" or "airdrop-a" or "airdrop-b" or "airdrop-estimate";
            var size = isPlayer ? 18 : isAirdropEstimate ? 17 : isBtr ? 16 : isAirdropBearing ? 13 : isSeasonDocument ? 13 : isKeyRoom ? 12 : marker.Type == "peer" ? 12 : isLabeledPoi ? 10 : 8;
            var brush = marker.Type switch
            {
                "player" => (SolidColorBrush)FindResource("PlayerBrush"),
                "player-stale" => (SolidColorBrush)FindResource("StalePlayerBrush"),
                "task" => (SolidColorBrush)FindResource("TaskBrush"),
                "extract" => (SolidColorBrush)FindResource("GreenBrush"),
                "transit" => (SolidColorBrush)FindResource("AmberBrush"),
                "key-room" => (SolidColorBrush)FindResource("KeyRoomBrush"),
                "switch" => (SolidColorBrush)FindResource("SwitchBrush"),
                "season-document" => (SolidColorBrush)FindResource("SeasonDocumentBrush"),
                "airdrop-a" => (SolidColorBrush)FindResource("AirdropABrush"),
                "airdrop-b" => (SolidColorBrush)FindResource("AirdropBBrush"),
                "airdrop-estimate" => (SolidColorBrush)FindResource("AirdropEstimateBrush"),
                "btr" => ResolveMarkerBrush(marker.ColorHex, "AmberBrush"),
                "peer" => ResolveMarkerBrush(marker.ColorHex, "AmberBrush"),
                _ => (SolidColorBrush)FindResource("TextDimBrush")
            };
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(isPlayer || isBtr || isAirdropBearing || isAirdropEstimate ? (byte)238 : marker.Type == "peer" ? (byte)205 : (byte)155, brush.Color.R, brush.Color.G, brush.Color.B)),
                Stroke = brush,
                StrokeThickness = isPlayer || isAirdropEstimate ? 2.5 : isBtr || isAirdropBearing || marker.Type == "peer" ? 2 : isKeyRoom ? 1.5 : 1
            };
            MarkerToolTipFactory.Attach(dot, marker);
            FrameworkElement markerVisual = dot;
            ExtractLabelLayout? extractLabelLayout = null;
            if (isLabeledPoi && marker.ShowLabel)
            {
                var label = new Border
                {
                    Padding = new Thickness(4, 2, 4, 2),
                    Background = new SolidColorBrush(Color.FromArgb(238, 14, 20, 18)),
                    BorderBrush = isBtr ? brush : (Brush)FindResource("LineBrightBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        Text = marker.Label,
                        Foreground = (Brush)FindResource("TextBrush"),
                        FontFamily = new FontFamily("Microsoft YaHei UI"),
                        FontSize = 9,
                        MaxWidth = 132,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                MarkerToolTipFactory.Attach(label, marker);
                var leader = new Line
                {
                    Stroke = brush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    Opacity = .65,
                    IsHitTestVisible = false
                };
                var host = new Canvas { Width = size, Height = size, ClipToBounds = false };
                Canvas.SetLeft(dot, 0);
                Canvas.SetTop(dot, 0);
                host.Children.Add(leader);
                host.Children.Add(dot);
                if (isBtr)
                {
                    var letter = new TextBlock
                    {
                        Text = "B",
                        Width = size,
                        Height = size,
                        Foreground = Brushes.Black,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        TextAlignment = TextAlignment.Center,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(letter, 0);
                    Canvas.SetTop(letter, 1);
                    host.Children.Add(letter);
                }
                else if (isAirdropBearing || isAirdropEstimate)
                {
                    var symbol = new TextBlock
                    {
                        Text = isAirdropEstimate ? "×" : marker.Type == "airdrop-a" ? "A" : "B",
                        Width = size,
                        Height = size,
                        Foreground = Brushes.White,
                        FontSize = isAirdropEstimate ? 12 : 8,
                        FontWeight = FontWeights.Bold,
                        TextAlignment = TextAlignment.Center,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(symbol, 0);
                    Canvas.SetTop(symbol, isAirdropEstimate ? -1 : 1);
                    host.Children.Add(symbol);
                }
                else if (isSeasonDocument)
                {
                    var symbol = new TextBlock
                    {
                        Text = "文",
                        Width = size,
                        Height = size,
                        Foreground = Brushes.White,
                        FontSize = 7,
                        FontWeight = FontWeights.Bold,
                        TextAlignment = TextAlignment.Center,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(symbol, 0);
                    Canvas.SetTop(symbol, 1);
                    host.Children.Add(symbol);
                }
                host.Children.Add(label);
                markerVisual = host;
                extractLabelLayout = new ExtractLabelLayout(host, label, leader, marker, size);
            }
            else if (isKeyRoom)
            {
                var host = new Grid { Width = size, Height = size };
                host.Children.Add(dot);
                host.Children.Add(new Viewbox
                {
                    Width = 8,
                    Height = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Child = new Path
                    {
                        Data = LockMarkerGeometry,
                        Fill = Brushes.Transparent,
                        Stroke = Brushes.White,
                        StrokeThickness = 1.7,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Stretch = Stretch.Uniform
                    }
                });
                markerVisual = host;
            }
            else if (isPlayer && marker.HeadingDegrees is { } heading)
            {
                var host = new Grid { Width = size, Height = size, ToolTip = marker.Label };
                host.Children.Add(dot);
                host.Children.Add(new Viewbox
                {
                    Width = 10,
                    Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Path { Data = DirectionArrowGeometry, Fill = Brushes.White, Stretch = Stretch.Uniform },
                    RenderTransformOrigin = new Point(.5, .5),
                    RenderTransform = new RotateTransform(heading)
                });
                markerVisual = host;
            }
            canvas.Children.Add(markerVisual);
            renderedMarkers.Add(new RenderedMiniMarker(markerVisual, marker, size, extractLabelLayout));
        }
    }

    private void QueueReposition(bool arrangeLabels)
    {
        _arrangeLabelsAfterReposition |= arrangeLabels;
        if (_repositionQueued) return;
        _repositionQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _repositionQueued = false;
            var arrange = _arrangeLabelsAfterReposition;
            _arrangeLabelsAfterReposition = false;
            RepositionMarkers(arrange);
        });
    }

    private void RepositionMarkers(bool arrangeLabels = true)
    {
        if (MiniMarkerCanvas is null || MiniDynamicMarkerCanvas is null || MapHost.ActualWidth < 1 || MapHost.ActualHeight < 1) return;
        if (_staticRenderedMarkers.Count == 0 && _staticMarkers.Count > 0)
        {
            RedrawMarkerLayer(MiniMarkerCanvas, _staticRenderedMarkers, _staticMarkers);
        }
        if (_dynamicRenderedMarkers.Count == 0 && _dynamicMarkers.Count > 0)
            RedrawMarkerLayer(MiniDynamicMarkerCanvas, _dynamicRenderedMarkers, _dynamicMarkers);

        _extractLabelLayouts.Clear();
        var imageBounds = GetTransformedImageBounds();
        RepositionAirdropRays(imageBounds);
        var viewportBounds = new Rect(0, 0, MapHost.ActualWidth, MapHost.ActualHeight);
        var labelBounds = Rect.Intersect(imageBounds, viewportBounds);
        if (labelBounds.IsEmpty) labelBounds = viewportBounds;
        foreach (var rendered in EnumerateRenderedMarkers())
        {
            var projected = ProjectMarker(rendered.Marker);
            var markerX = imageBounds.X + projected.X * imageBounds.Width;
            var markerY = imageBounds.Y + projected.Y * imageBounds.Height;
            var outsideViewport = markerX < -rendered.Size || markerX > MapHost.ActualWidth + rendered.Size ||
                                  markerY < -rendered.Size || markerY > MapHost.ActualHeight + rendered.Size;
            rendered.Element.Visibility = outsideViewport ? Visibility.Collapsed : Visibility.Visible;
            if (outsideViewport) continue;

            Canvas.SetLeft(rendered.Element, markerX - rendered.Size / 2d);
            Canvas.SetTop(rendered.Element, markerY - rendered.Size / 2d);
            if (rendered.LabelLayout is not null) _extractLabelLayouts.Add(rendered.LabelLayout);
        }

        if (arrangeLabels) ArrangeExtractLabels(labelBounds);
    }

    private void RepositionAirdropRays(Rect imageBounds)
    {
        if (MiniAirdropRayCanvas is null) return;
        MiniAirdropRayCanvas.Children.Clear();
        if (_airdropOverlay?.FirstRay is { } first)
            AddAirdropRay(first, imageBounds, (Brush)FindResource("AirdropABrush"), [5, 3]);
        if (_airdropOverlay?.SecondRay is { } second)
            AddAirdropRay(second, imageBounds, (Brush)FindResource("AirdropBBrush"), [3, 3]);
    }

    private void AddAirdropRay(AirdropMapRay ray, Rect imageBounds, Brush brush, DoubleCollection dash)
    {
        var start = _markerProjection.Transform(ray.StartX, ray.StartY);
        var end = _markerProjection.Transform(ray.EndX, ray.EndY);
        var x1 = imageBounds.X + start.X * imageBounds.Width;
        var y1 = imageBounds.Y + start.Y * imageBounds.Height;
        var x2 = imageBounds.X + end.X * imageBounds.Width;
        var y2 = imageBounds.Y + end.Y * imageBounds.Height;
        MiniAirdropRayCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 5,
            Opacity = .16,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        MiniAirdropRayCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 1.6,
            StrokeDashArray = dash,
            Opacity = .95,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
    }

    private IEnumerable<RenderedMiniMarker> EnumerateRenderedMarkers() =>
        _staticRenderedMarkers.Concat(_dynamicRenderedMarkers);

    private (double X, double Y) ProjectMarker(MapMarker marker) =>
        MapBaseProjectionService.ProjectMarker(marker, _mapId, _mapStyle, _markerProjection);

    private static string BuildMarkerSignature(IEnumerable<MapMarker> markers) => string.Join(
        "\u001e",
        markers.Select(marker => string.Join(
            "\u001f",
            marker.Type,
            marker.Label,
            marker.ToolTipText,
            marker.PreviewImageId,
            marker.PreviewImageUrl,
            marker.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            marker.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            marker.WorldX?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            marker.WorldZ?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            marker.HeadingDegrees?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            marker.ColorHex,
            marker.ShowLabel)));

    private void ArrangeExtractLabels(Rect imageBounds)
    {
        if (_extractLabelLayouts.Count == 0) return;

        var placed = new RectSpatialIndex();
        foreach (var layout in _extractLabelLayouts.OrderBy(item => ProjectMarker(item.Marker).Y)
                     .ThenBy(item => ProjectMarker(item.Marker).X))
        {
            layout.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = Math.Min(140, Math.Max(24, layout.Label.DesiredSize.Width));
            var labelHeight = Math.Max(16, layout.Label.DesiredSize.Height);
            var hostLeft = Canvas.GetLeft(layout.Host);
            var hostTop = Canvas.GetTop(layout.Host);
            var preferredLeft = ProjectMarker(layout.Marker).X >= .62;
            var best = default((bool PlaceLeft, double Left, double Top, Rect Bounds, double Score));
            var hasBest = false;

            foreach (var placeLeft in new[] { preferredLeft, !preferredLeft })
            {
                foreach (var offset in EnumerateLabelOffsets())
                {
                    var rawLeft = placeLeft
                        ? hostLeft - labelWidth - 6
                        : hostLeft + layout.DotSize + 6;
                    var left = Math.Clamp(rawLeft, imageBounds.Left + 2, Math.Max(imageBounds.Left + 2, imageBounds.Right - labelWidth - 2));
                    var rawTop = hostTop + (layout.DotSize - labelHeight) / 2 + offset;
                    var top = Math.Clamp(rawTop, imageBounds.Top + 2, Math.Max(imageBounds.Top + 2, imageBounds.Bottom - labelHeight - 2));
                    var bounds = new Rect(left, top, labelWidth, labelHeight);
                    var collisionBounds = bounds;
                    collisionBounds.Inflate(3, 2);
                    var collisions = placed.CountIntersections(collisionBounds);
                    var score = collisions * 10_000 + Math.Abs(offset) + (placeLeft == preferredLeft ? 0 : 24);
                    if (!hasBest || score < best.Score)
                    {
                        best = (placeLeft, left, top, bounds, score);
                        hasBest = true;
                    }
                    if (collisions == 0) break;
                }
                if (hasBest && best.Score < 10_000) break;
            }

            if (!hasBest) continue;
            layout.Label.Width = labelWidth;
            Canvas.SetLeft(layout.Label, best.Left - hostLeft);
            Canvas.SetTop(layout.Label, best.Top - hostTop);
            layout.Leader.X1 = layout.DotSize / 2;
            layout.Leader.Y1 = layout.DotSize / 2;
            layout.Leader.X2 = (best.PlaceLeft ? best.Left + labelWidth : best.Left) - hostLeft;
            layout.Leader.Y2 = best.Top + labelHeight / 2 - hostTop;
            layout.Leader.Visibility = Visibility.Visible;
            var placedBounds = best.Bounds;
            placedBounds.Inflate(3, 2);
            placed.Add(placedBounds);
        }
    }

    private static IEnumerable<double> EnumerateLabelOffsets()
    {
        yield return 0;
        for (var step = 1; step <= 10; step++)
        {
            yield return step * 16;
            yield return step * -16;
        }
    }

    private SolidColorBrush ResolveMarkerBrush(string? colorHex, string fallbackResourceKey)
    {
        var fallback = (SolidColorBrush)FindResource(fallbackResourceKey);
        if (string.IsNullOrWhiteSpace(colorHex)) return fallback;
        try
        {
            return ColorConverter.ConvertFromString(colorHex) is Color color
                ? new SolidColorBrush(color)
                : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private Rect GetImageDisplayBounds()
    {
        if (MiniMapImage.Source is not BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } source)
            return new Rect(0, 0, MapHost.ActualWidth, MapHost.ActualHeight);

        var scale = Math.Min(MapHost.ActualWidth / source.PixelWidth, MapHost.ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        return new Rect((MapHost.ActualWidth - width) / 2, (MapHost.ActualHeight - height) / 2, width, height);
    }

    private Rect GetTransformedImageBounds()
    {
        var bounds = GetImageDisplayBounds();
        var centerX = MapHost.ActualWidth / 2d;
        var centerY = MapHost.ActualHeight / 2d;
        return new Rect(
            centerX + (bounds.X - centerX) * _zoom + _panX,
            centerY + (bounds.Y - centerY) * _zoom + _panY,
            bounds.Width * _zoom,
            bounds.Height * _zoom);
    }
}
