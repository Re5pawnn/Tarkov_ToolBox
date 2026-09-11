using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using TarkovMapLocator.Modules.InGamePrice.Models;
using DrawingSize = System.Drawing.Size;

namespace TarkovMapLocator.Modules.InGamePrice.Services;

/// <summary>
/// Fast, OCR-free capture path for the debug view. It intentionally does
/// not load OpenCV or PP-OCR, so the live debug image can refresh between two
/// normal recognition passes without competing for the recognizer instance.
/// </summary>
public static class InGamePriceDebugFrameCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static InGamePriceDebugFrame Capture(
        InGamePriceRecognitionSettings settings,
        InGamePriceDebugGeometry? geometry)
    {
        var normalized = settings.Normalize();
        // Mirror the recognizer's bounds choice: a user-boxed recognition area
        // wins in automatic mode too, so the debug viewport shows exactly what
        // the recognizer scans.
        var bounds = normalized.CaptureRegion is { IsUsable: true } configured
            ? configured.Normalize()
            : GetAutomaticSearchBounds(normalized);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.X,
                bounds.Y,
                0,
                0,
                new DrawingSize(bounds.Width, bounds.Height),
                CopyPixelOperation.SourceCopy);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawGeometry(graphics, geometry);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new InGamePriceDebugFrame(stream.ToArray(), bitmap.Width, bitmap.Height, geometry);
    }

    private static void DrawGeometry(
        Graphics graphics,
        InGamePriceDebugGeometry? geometry)
    {
        if (geometry is null) return;

        using var templatePen = new Pen(Color.FromArgb(246, 91, 91), 2);
        using var ocrPen = new Pen(Color.FromArgb(78, 204, 126), 2);
        using var overlayPen = new Pen(Color.FromArgb(79, 169, 255), 2);
        using var textPen = new Pen(Color.FromArgb(255, 211, 77), 2);
        using var labelBrush = new SolidBrush(Color.White);
        using var font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold, GraphicsUnit.Pixel);

        DrawGuide(graphics, templatePen, labelBrush, font, geometry.Template, "物品标题");
        DrawGuide(graphics, ocrPen, labelBrush, font, geometry.Ocr, "重量 kg");
        DrawGuide(graphics, overlayPen, labelBrush, font, geometry.Overlay, "价签");
        DrawGuide(graphics, textPen, labelBrush, font, geometry.OverlayText, "文字");
    }

    private static void DrawGuide(
        Graphics graphics,
        Pen pen,
        Brush labelBrush,
        Font font,
        InGamePriceDebugRectangle? requested,
        string label)
    {
        if (requested is not { IsUsable: true }) return;
        var canvas = new Rectangle(0, 0, (int)graphics.VisibleClipBounds.Width, (int)graphics.VisibleClipBounds.Height);
        var bounds = Rectangle.Intersect(canvas, ToDrawingRectangle(requested));
        if (bounds.Width < 1 || bounds.Height < 1) return;

        graphics.DrawRectangle(pen, bounds);
        var labelY = Math.Max(0, bounds.Y - font.Height - 3);
        using var labelBackground = new SolidBrush(Color.FromArgb(205, pen.Color));
        graphics.FillRectangle(labelBackground, bounds.X, labelY, Math.Max(28, label.Length * 18), font.Height + 3);
        graphics.DrawString(label, font, labelBrush, bounds.X + 3, labelY + 1);
    }

    private static Rectangle ToDrawingRectangle(InGamePriceDebugRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static ScreenCaptureRegion GetVirtualScreenBounds() => new(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        Math.Max(24, GetSystemMetrics(SmCxVirtualScreen)),
        Math.Max(24, GetSystemMetrics(SmCyVirtualScreen)));

    private static ScreenCaptureRegion GetAutomaticSearchBounds(InGamePriceRecognitionSettings settings)
    {
        var available = EftCaptureTargetService.TryGetWindowBounds() ?? GetVirtualScreenBounds();
        var width = Math.Min(settings.AutoSearchWidth, available.Width);
        var height = Math.Min(settings.AutoSearchHeight, available.Height);
        var point = EftCaptureTargetService.GetSearchPoint(available);
        var x = Math.Clamp(point.X - width / 2, available.X, available.X + available.Width - width);
        var y = Math.Clamp(point.Y - height / 2, available.Y, available.Y + available.Height - height);
        return new ScreenCaptureRegion(x, y, width, height);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
