using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Interop;

namespace TarkovMapLocator.Modules.Memo;

internal sealed class MemoOverlayWindow : Window
{
    private readonly StackPanel _content = new();
    private readonly Border _frame;
    private readonly double? _savedLeft;
    private readonly double? _savedTop;
    private bool _editMode;
    private bool _allowClose;

    public event EventHandler? BoundsCommitted;

    public MemoOverlayWindow(double? savedLeft, double? savedTop)
    {
        _savedLeft = savedLeft;
        _savedTop = savedTop;
        Width = 360;
        SizeToContent = SizeToContent.Height;
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

        _frame = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(13, 11, 13, 12),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _content
            }
        };
        Content = _frame;
        PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;
        SourceInitialized += (_, _) => ApplyWindowStyle();
        Loaded += (_, _) => PlaceOnScreen();
    }

    public void UpdateContent(string text, IReadOnlyList<MemoItem> items)
    {
        _content.Children.Clear();
        _content.Children.Add(Text("备忘录", 16, FontWeights.SemiBold, Brush("#E9AD50"), new Thickness()));
        if (!string.IsNullOrWhiteSpace(text))
            _content.Children.Add(Text(text.Trim(), 14, FontWeights.Normal, Brushes.White, new Thickness(0, 8, 0, 0)));
        if (items.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(text))
                _content.Children.Add(new Border { Height = 1, Background = Brush("#66425049"), Margin = new Thickness(0, 11, 0, 8) });
            foreach (var item in items)
                _content.Children.Add(Text($"• {item.Name}  × {item.Quantity}", 14, FontWeights.Normal, Brushes.White, new Thickness(0, 5, 0, 0)));
        }
    }

    public void SetEditMode(bool enabled)
    {
        _editMode = enabled;
        _frame.Background = enabled ? Brush("#EE0D1210") : Brushes.Transparent;
        _frame.BorderBrush = enabled ? Brush("#E9AD50") : Brushes.Transparent;
        _frame.Effect = enabled ? null : new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 4,
            ShadowDepth = 1,
            Opacity = .95
        };
        Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;
        ApplyWindowStyle();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        try
        {
            DragMove();
            BoundsCommitted?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PlaceOnScreen()
    {
        const double visibleMargin = 48;
        Left = Math.Clamp(
            _savedLeft ?? SystemParameters.WorkArea.Left + 18,
            SystemParameters.VirtualScreenLeft - Width + visibleMargin,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - visibleMargin);
        Top = Math.Clamp(
            _savedTop ?? SystemParameters.WorkArea.Top + 120,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - visibleMargin);
    }

    private void ApplyWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        style |= ToolWindowStyle | NoActivateStyle;
        style = _editMode ? style & ~TransparentStyle : style | TransparentStyle;
        style &= ~AppWindowStyle;
        SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(style));
    }

    private static TextBlock Text(string value, double size, FontWeight weight, Brush foreground, Thickness margin) => new()
    {
        Text = value,
        Foreground = foreground,
        FontFamily = new FontFamily("Microsoft YaHei UI"),
        FontSize = size,
        FontWeight = weight,
        LineHeight = 21,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin,
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = .95 }
    };

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private const int ExtendedStyleIndex = -20;
    private const long TransparentStyle = 0x00000020L;
    private const long ToolWindowStyle = 0x00000080L;
    private const long AppWindowStyle = 0x00040000L;
    private const long NoActivateStyle = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);
}
