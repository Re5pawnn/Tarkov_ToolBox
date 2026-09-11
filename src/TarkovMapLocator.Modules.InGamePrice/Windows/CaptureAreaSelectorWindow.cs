using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TarkovMapLocator.Modules.InGamePrice.Models;

namespace TarkovMapLocator.Modules.InGamePrice.Windows;

/// <summary>One-shot full-screen selector for shrinking recognition to the game window.</summary>
public sealed class CaptureAreaSelectorWindow : Window
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private readonly Canvas _canvas = new()
    {
        Background = Brushes.Transparent,
        Cursor = Cursors.Cross
    };
    private readonly Rectangle _selection = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(244, 181, 63)),
        StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.FromArgb(38, 244, 181, 63)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly double _dpiScale;
    private readonly ScreenCaptureRegion _desktopBounds;
    private Point? _dragStart;
    private PhysicalPoint? _physicalDragStart;

    public CaptureAreaSelectorWindow()
    {
        _desktopBounds = GetVirtualScreenBounds();
        _dpiScale = Math.Max(.75d, GetDpiForSystem() / 96d);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(92, 0, 0, 0));
        Opacity = 0;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _desktopBounds.X / _dpiScale;
        Top = _desktopBounds.Y / _dpiScale;
        Width = _desktopBounds.Width / _dpiScale;
        Height = _desktopBounds.Height / _dpiScale;

        var root = new Grid();
        root.Children.Add(_canvas);
        _canvas.Children.Add(_selection);
        root.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 22, 0, 0),
            Padding = new Thickness(13, 8, 13, 8),
            Background = new SolidColorBrush(Color.FromArgb(236, 12, 18, 17)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(73, 96, 87)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "拖拽框选游戏界面识别范围 · Esc 取消",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14
            }
        });
        Content = root;
        SourceInitialized += (_, _) => ApplyPhysicalDesktopBounds();
        PreviewKeyDown += Selector_KeyDown;
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _canvas.MouseRightButtonUp += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        _canvas.LostMouseCapture += (_, _) =>
        {
            _dragStart = null;
            _physicalDragStart = null;
        };
        Loaded += (_, _) =>
        {
            ApplyPhysicalDesktopBounds();
            Opacity = 1;
            Focus();
        };
    }

    public ScreenCaptureRegion? SelectedRegion { get; private set; }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_canvas);
        _physicalDragStart = GetPhysicalCursorPosition(_dragStart.Value);
        _canvas.CaptureMouse();
        _selection.Visibility = Visibility.Visible;
        DrawSelection(_dragStart.Value, _dragStart.Value);
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || !_canvas.IsMouseCaptured) return;
        DrawSelection(start, e.GetPosition(_canvas));
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var end = e.GetPosition(_canvas);
        var screenStart = _physicalDragStart ?? GetPhysicalCursorPosition(start);
        var screenEnd = GetPhysicalCursorPosition(end);
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        _dragStart = null;
        _physicalDragStart = null;
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        if (width < 24 || height < 24)
        {
            _selection.Visibility = Visibility.Collapsed;
            return;
        }

        // GetCursorPos is always in physical desktop pixels, exactly what
        // Graphics.CopyFromScreen expects.  This avoids DPI virtualization and
        // keeps the selected range correct on mixed-DPI multi-monitor setups.
        SelectedRegion = new ScreenCaptureRegion(
            Math.Min(screenStart.X, screenEnd.X),
            Math.Min(screenStart.Y, screenEnd.Y),
            Math.Abs(screenEnd.X - screenStart.X),
            Math.Abs(screenEnd.Y - screenStart.Y)).Normalize();
        DialogResult = true;
        Close();
    }

    private void DrawSelection(Point first, Point second)
    {
        var x = Math.Min(first.X, second.X);
        var y = Math.Min(first.Y, second.Y);
        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = Math.Abs(second.X - first.X);
        _selection.Height = Math.Abs(second.Y - first.Y);
    }

    private void Selector_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        Close();
    }

    private static ScreenCaptureRegion GetVirtualScreenBounds() => new(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        Math.Max(24, GetSystemMetrics(SmCxVirtualScreen)),
        Math.Max(24, GetSystemMetrics(SmCyVirtualScreen)));

    private void ApplyPhysicalDesktopBounds()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !SetWindowPos(
                handle,
                IntPtr.Zero,
                _desktopBounds.X,
                _desktopBounds.Y,
                _desktopBounds.Width,
                _desktopBounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
        {
            Left = _desktopBounds.X / _dpiScale;
            Top = _desktopBounds.Y / _dpiScale;
            Width = _desktopBounds.Width / _dpiScale;
            Height = _desktopBounds.Height / _dpiScale;
        }
    }

    private PhysicalPoint GetPhysicalCursorPosition(Point fallback)
    {
        if (GetCursorPos(out var point)) return new PhysicalPoint(point.X, point.Y);
        var screenPoint = _canvas.PointToScreen(fallback);
        return new PhysicalPoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
    }

    private readonly record struct PhysicalPoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
