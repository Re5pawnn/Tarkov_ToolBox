using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TarkovMapLocator.Modules.InGamePrice.Models;
using TarkovMapLocator.Modules.InGamePrice.Services;

namespace TarkovMapLocator.Modules.InGamePrice.Windows;

/// <summary>
/// Click-through, no-activation price tag shown beside the in-game item tooltip.
/// It intentionally has no taskbar entry and never captures game input.
/// </summary>
public sealed class InGamePriceOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private readonly TextBlock _content;
    private string? _shownItemId;
    private InGamePriceOverlayBounds _shownBounds;

    // Keep these frozen and shared just like the original OCR companion.  The
    // current item's per-slot value selects one colour for the whole tag.
    private static readonly SolidColorBrush DefaultGrayBrush = CreateBrush(Color.FromRgb(180, 180, 180));
    private static readonly SolidColorBrush LightBlueBrush = CreateBrush(Color.FromRgb(130, 207, 255));
    private static readonly SolidColorBrush PurpleBrush = CreateBrush(Color.FromRgb(197, 139, 255));
    private static readonly SolidColorBrush GoldBrush = CreateBrush(Color.FromRgb(255, 215, 0));
    private static readonly SolidColorBrush RedBrush = CreateBrush(Color.FromRgb(255, 107, 107));
    private static readonly SolidColorBrush PinkBrush = CreateBrush(Color.FromRgb(255, 133, 192));

    public InGamePriceOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;
        IsHitTestVisible = false;

        _content = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 15,
            LineHeight = 20,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        // The original OCR tool deliberately renders only the coloured text.
        // A transparent WPF window lets the game's own tooltip remain fully
        // visible underneath, without a card, border, or colour accent bar.
        Content = _content;
        SourceInitialized += (_, _) => EnableClickThrough();
    }

    public void ShowMatch(InGamePriceRecognitionSettings settings, InGamePriceMatch match, int templateX, int templateY, double anchorScale = 1d)
    {
        var normalized = settings.Normalize();
        var bounds = InGamePriceOverlayPlacement.Resolve(normalized, templateX, templateY, anchorScale);
        _content.Text = InGamePriceOverlayFormatter.BuildText(match);
        _content.Foreground = PriceBrush(InGamePriceOverlayFormatter.GetEffectivePerSlotPrice(match.Item));

        // The detector's boxes wobble a pixel or two from frame to frame, and
        // repositioning the window every scan makes the text visibly shiver.
        // While the same item stays within this deadband, leave the window
        // exactly where it is and only refresh the text.
        var deadband = Math.Max(6, (int)Math.Round(12 * Math.Clamp(anchorScale, 0.35, 2.25)));
        if (!IsVisible)
        {
            // Show invisibly first so the native handle exists before we place it
            // in the recognizer's physical-pixel coordinate space.
            Opacity = 0;
            Left = bounds.X;
            Top = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
            Show();
        }

        ApplyPhysicalBounds(bounds);
        ApplyContentMetrics(normalized, bounds);

        if (string.Equals(_shownItemId, match.Item.Id, StringComparison.Ordinal) &&
            Math.Abs(bounds.X - _shownBounds.X) <= deadband &&
            Math.Abs(bounds.Y - _shownBounds.Y) <= deadband &&
            bounds.Width == _shownBounds.Width &&
            bounds.Height == _shownBounds.Height)
        {
            Opacity = 1;
            return;
        }

        _shownItemId = match.Item.Id;
        _shownBounds = bounds;
        Opacity = 1;
    }

    public void HidePrice()
    {
        // Clear both the retained visual and the native surface state before
        // hiding. This makes a late frame harmless even during a stop race.
        _content.Text = string.Empty;
        _shownItemId = null;
        Opacity = 0;
        if (IsVisible) Hide();
    }

    private void EnableClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var current = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(current | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    private void ApplyPhysicalBounds(InGamePriceOverlayBounds bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !SetWindowPos(
                handle,
                IntPtr.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
        {
            // Native placement should always be available on supported Windows
            // versions. Keep a WPF fallback so an overlay still appears if it is
            // blocked by a future platform policy.
            Left = bounds.X;
            Top = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
        }
    }

    /// <summary>
    /// The recognizer locates the game UI in physical pixels, whereas WPF lays
    /// out TextBlock values in device-independent pixels. Previously a 200%
    /// desktop scale made a 307×106 physical-pixel tag behave like a 153×53
    /// DIP text area, so price lines were visibly clipped. Keep the outer
    /// window in physical pixels but convert the inner typography and padding
    /// back to DIPs for the monitor hosting the tag.
    /// </summary>
    private void ApplyContentMetrics(InGamePriceRecognitionSettings settings, InGamePriceOverlayBounds bounds)
    {
        var (scaleX, scaleY) = GetWindowDpiScale();
        var marginX = settings.OverlayTextOffsetX / scaleX;
        var marginY = settings.OverlayTextOffsetY / scaleY;
        var width = Math.Max(1, bounds.Width / scaleX - Math.Max(0, marginX));
        var height = Math.Max(1, bounds.Height / scaleY - Math.Max(0, marginY));
        var lineHeight = Math.Ceiling(settings.OverlayFontSize * 1.35) / scaleY;

        _content.FontSize = settings.OverlayFontSize / scaleY;
        _content.LineHeight = Math.Max(_content.FontSize, lineHeight);
        _content.Margin = new Thickness(marginX, marginY, 0, 0);
        _content.Width = width;
        _content.Height = height;
    }

    private (double ScaleX, double ScaleY) GetWindowDpiScale()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(handle);
            if (dpi >= 48) return (dpi / 96d, dpi / 96d);
        }

        var dpiInfo = VisualTreeHelper.GetDpi(this);
        return (Math.Max(.5, dpiInfo.DpiScaleX), Math.Max(.5, dpiInfo.DpiScaleY));
    }

    private static SolidColorBrush PriceBrush(int perSlotPrice) =>
        perSlotPrice switch
        {
            <= 0 => DefaultGrayBrush,
            < 100_000 => LightBlueBrush,
            < 500_000 => PurpleBrush,
            < 1_000_000 => GoldBrush,
            < 10_000_000 => RedBrush,
            _ => PinkBrush
        };

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
}
