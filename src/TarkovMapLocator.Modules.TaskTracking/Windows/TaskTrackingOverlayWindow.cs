using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using TarkovMapLocator.Modules.TaskTracking.Models;

namespace TarkovMapLocator.Modules.TaskTracking.Windows;

public sealed class TaskTrackingOverlayWindow : Window
{
    private const double MinimumOverlayWidth = 260;
    private const double MinimumOverlayHeight = 140;
    private readonly StackPanel _taskPanel = new();
    private readonly Border _frame;
    private readonly List<Thumb> _resizeHandles = [];
    private readonly double? _savedLeft;
    private readonly double? _savedTop;
    private readonly bool _hasSavedSize;
    private bool _isEditMode;
    private bool _allowPermanentClose;

    public event EventHandler? BoundsCommitted;

    public TaskTrackingOverlayWindow(double? savedLeft, double? savedTop, double? savedWidth, double? savedHeight)
    {
        _savedLeft = savedLeft;
        _savedTop = savedTop;
        _hasSavedSize = savedWidth is not null || savedHeight is not null;
        Width = savedWidth ?? 350;
        MinWidth = MinimumOverlayWidth;
        MinHeight = MinimumOverlayHeight;
        if (savedHeight is { } height)
        {
            Height = height;
            SizeToContent = SizeToContent.Manual;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
        }
        MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height * .82);
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true,
            Content = _taskPanel
        };
        _frame = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 11, 10, 11),
            Child = scrollViewer
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_frame);
        AddResizeHandles(root);
        Content = root;
        PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            var requestedLeft = _savedLeft ?? workArea.Left + 18;
            var requestedTop = _savedTop ?? workArea.Top + 120;
            const double visibleMargin = 48;
            Left = Math.Clamp(
                requestedLeft,
                SystemParameters.VirtualScreenLeft - Math.Max(Width, MinimumOverlayWidth) + visibleMargin,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - visibleMargin);
            Top = Math.Clamp(
                requestedTop,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - visibleMargin);
            if (!_hasSavedSize && SizeToContent == SizeToContent.Manual)
                Height = Math.Max(MinimumOverlayHeight, ActualHeight);
        };
    }

    public bool IsEditMode => _isEditMode;
    public double PersistedWidth => Math.Max(MinimumOverlayWidth, ActualWidth > 0 ? ActualWidth : Width);
    public double PersistedHeight => Math.Max(MinimumOverlayHeight, ActualHeight > 0 ? ActualHeight : Height);

    public void ClosePermanently()
    {
        _allowPermanentClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowPermanentClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void SetEditMode(bool enabled)
    {
        _isEditMode = enabled;
        if (enabled && SizeToContent != SizeToContent.Manual)
        {
            var currentHeight = Math.Max(MinimumOverlayHeight, ActualHeight);
            SizeToContent = SizeToContent.Manual;
            Height = currentHeight;
        }
        _frame.Background = enabled ? Brush("#EE0D1210") : Brushes.Transparent;
        _frame.BorderBrush = enabled ? Brush("#E9AD50") : Brushes.Transparent;
        _frame.Effect = enabled ? null : new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 4,
            ShadowDepth = 1,
            Opacity = .95
        };
        foreach (var handle in _resizeHandles)
            handle.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;
    }

    public void UpdateTasks(IReadOnlyList<TaskTrackingTask> tasks)
    {
        _taskPanel.Children.Clear();
        for (var index = 0; index < tasks.Count; index++)
        {
            if (index > 0)
            {
                _taskPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush("#66425049"),
                    Margin = new Thickness(2, 11, 4, 11)
                });
            }
            _taskPanel.Children.Add(BuildTask(tasks[index]));
        }
    }

    private static FrameworkElement BuildTask(TaskTrackingTask task)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = task.Name,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var objectives = task.Objectives.Count > 0 ? task.Objectives : ["暂无任务目标说明"];
        foreach (var objective in objectives)
        {
            panel.Children.Add(new TextBlock
            {
                Text = objective,
                Foreground = Brush("#E7ECE9"),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 4, 0)
            });
        }
        return panel;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        try
        {
            DragMove();
            BoundsCommitted?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException)
        {
            // A late mouse transition can arrive while the application is closing.
        }
    }

    private void AddResizeHandles(Grid root)
    {
        AddResizeHandle(root, ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE);
        AddResizeHandle(root, ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE);
        AddResizeHandle(root, ResizeEdge.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 8, Cursors.SizeNS);
        AddResizeHandle(root, ResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, Cursors.SizeNS);
        AddResizeHandle(root, ResizeEdge.TopLeft, HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14, Cursors.SizeNWSE);
        AddResizeHandle(root, ResizeEdge.TopRight, HorizontalAlignment.Right, VerticalAlignment.Top, 14, 14, Cursors.SizeNESW);
        AddResizeHandle(root, ResizeEdge.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom, 14, 14, Cursors.SizeNESW);
        AddResizeHandle(root, ResizeEdge.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom, 14, 14, Cursors.SizeNWSE);
    }

    private void AddResizeHandle(
        Grid root,
        ResizeEdge edge,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        double width,
        double height,
        Cursor cursor)
    {
        var handle = new Thumb
        {
            Tag = edge,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Width = double.IsNaN(width) ? double.NaN : width,
            Height = double.IsNaN(height) ? double.NaN : height,
            Background = Brushes.Transparent,
            Cursor = cursor,
            Visibility = Visibility.Collapsed
        };
        handle.DragStarted += (_, _) =>
        {
            if (SizeToContent != SizeToContent.Manual)
            {
                Height = Math.Max(MinimumOverlayHeight, ActualHeight);
                SizeToContent = SizeToContent.Manual;
            }
        };
        handle.DragDelta += ResizeHandle_DragDelta;
        handle.DragCompleted += (_, _) => BoundsCommitted?.Invoke(this, EventArgs.Empty);
        Panel.SetZIndex(handle, 2);
        root.Children.Add(handle);
        _resizeHandles.Add(handle);
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isEditMode || sender is not Thumb { Tag: ResizeEdge edge }) return;
        var horizontal = e.HorizontalChange;
        var vertical = e.VerticalChange;

        if (edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            var nextWidth = Math.Max(MinimumOverlayWidth, ActualWidth - horizontal);
            Left += ActualWidth - nextWidth;
            Width = nextWidth;
        }
        if (edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
            Width = Math.Max(MinimumOverlayWidth, ActualWidth + horizontal);
        if (edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            var nextHeight = Math.Max(MinimumOverlayHeight, ActualHeight - vertical);
            Top += ActualHeight - nextHeight;
            Height = nextHeight;
        }
        if (edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            Height = Math.Max(MinimumOverlayHeight, ActualHeight + vertical);
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollBar or Thumb) return true;
        return false;
    }

    private void ApplyToolWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        style |= ToolWindowStyle | NoActivateStyle;
        style &= ~AppWindowStyle;
        SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(style));
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private enum ResizeEdge
    {
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private const int ExtendedStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080L;
    private const long AppWindowStyle = 0x00040000L;
    private const long NoActivateStyle = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);
}
