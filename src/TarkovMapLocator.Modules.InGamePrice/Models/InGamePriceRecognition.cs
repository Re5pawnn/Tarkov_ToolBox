using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.InGamePrice.Models;

/// <summary>
/// Physical-pixel capture bounds. A null value means the entire virtual desktop.
/// </summary>
public sealed record ScreenCaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsUsable => Width >= 24 && Height >= 24;

    public ScreenCaptureRegion Normalize() => new(X, Y, Math.Clamp(Width, 24, 16384), Math.Clamp(Height, 24, 16384));
}

/// <summary>
/// Desktop-only settings for the in-game item recognizer. They deliberately live
/// separately from the map and log preferences so a bad calibration never blocks
/// the map from starting.
/// </summary>
public sealed record InGamePriceRecognitionSettings(
    ScreenCaptureRegion? CaptureRegion = null,
    int OverlayRelativeX = 350,
    int OverlayRelativeY = 0,
    // The tag contains five text lines. Keep a physical-pixel safety margin so
    // it remains legible even when a game uses a long localized item name.
    int OverlayWidth = 380,
    int OverlayHeight = 175,
    int OverlayFontSize = 15,
    int OverlayTextOffsetX = 0,
    int OverlayTextOffsetY = 0,
    int ScanIntervalMilliseconds = 220,
    double MatchSimilarity = 0.72,
    int AutoSearchWidth = 960,
    int AutoSearchHeight = 720,
    bool IsCaptureRegionUserSelected = false)
{
    public static InGamePriceRecognitionSettings Default { get; } = new();

    public InGamePriceRecognitionSettings Normalize()
    {
        var normalizedRegion = CaptureRegion is { IsUsable: true } region ? region.Normalize() : null;
        return this with
        {
            CaptureRegion = normalizedRegion,
            IsCaptureRegionUserSelected = normalizedRegion is not null && IsCaptureRegionUserSelected,
            OverlayRelativeX = Math.Clamp(OverlayRelativeX, -4000, 4000),
            OverlayRelativeY = Math.Clamp(OverlayRelativeY, -4000, 4000),
            // Older seeds used a 307 × 106 box. That was only just large
            // enough at 100% desktop scaling and clipped the five-line tag on
            // high-DPI displays. Normalizing also upgrades existing profiles
            // without moving their calibrated anchor point.
            OverlayWidth = Math.Clamp(OverlayWidth, 380, 720),
            OverlayHeight = Math.Clamp(OverlayHeight, 175, 420),
            OverlayFontSize = Math.Clamp(OverlayFontSize, 11, 28),
            OverlayTextOffsetX = Math.Clamp(OverlayTextOffsetX, -180, 180),
            OverlayTextOffsetY = Math.Clamp(OverlayTextOffsetY, -120, 120),
            ScanIntervalMilliseconds = Math.Clamp(ScanIntervalMilliseconds, 90, 2000),
            MatchSimilarity = Math.Clamp(MatchSimilarity, 0.40, 1.0),
            AutoSearchWidth = Math.Clamp(AutoSearchWidth, 480, 1920),
            AutoSearchHeight = Math.Clamp(AutoSearchHeight, 360, 1440)
        };
    }
}

/// <summary>
/// Physical-pixel bounds for the click-through overlay.  The recognizer and
/// Graphics.CopyFromScreen both operate in physical desktop pixels, so this
/// conversion deliberately does not involve WPF device-independent units.
/// </summary>
public readonly record struct InGamePriceOverlayBounds(int X, int Y, int Width, int Height)
{
    public bool IsUsable => Width > 0 && Height > 0;
}

public static class InGamePriceOverlayPlacement
{
    public static InGamePriceOverlayBounds Resolve(
        InGamePriceRecognitionSettings settings,
        int templateX,
        int templateY,
        double anchorScale = 1d)
    {
        var normalized = settings.Normalize();
        var scale = Math.Clamp(anchorScale, 0.35, 2.25);
        return new InGamePriceOverlayBounds(
            templateX + (int)Math.Round(normalized.OverlayRelativeX * scale),
            templateY + (int)Math.Round(normalized.OverlayRelativeY * scale),
            normalized.OverlayWidth,
            normalized.OverlayHeight);
    }
}

public sealed record InGamePriceRecognitionSample(
    bool TemplateFound,
    string OcrText,
    int TemplateX,
    int TemplateY,
    double TemplateScore,
    string? Issue = null,
    InGamePriceDebugFrame? DebugFrame = null,
    InGamePriceDebugGeometry? Geometry = null,
    double AnchorScale = 1d,
    IReadOnlyList<InGamePriceOcrCandidate>? Candidates = null);

/// <summary>
/// One recognized text line from the detection-based automatic pipeline, in
/// physical desktop pixels.  The overlay anchors to the LEFT edge of the title
/// line and hangs below it (via OverlayRelativeY), which lands the price text
/// on the window's dark image pane instead of over the title bar and close
/// button; AnchorScale keys off the measured line height (22 px is the
/// 100%-UI reference height).
/// </summary>
public sealed record InGamePriceOcrCandidate(
    string Text,
    double Confidence,
    int PhysicalX,
    int PhysicalY,
    int Width,
    int Height)
{
    public int AnchorX => PhysicalX;
    public int AnchorY => PhysicalY;
    public double AnchorScale => Math.Clamp(Height / 22.0, 0.35, 2.25);
}

/// <summary>Physical-pixel rectangle inside the captured debug image.</summary>
public sealed record InGamePriceDebugRectangle(int X, int Y, int Width, int Height)
{
    public bool IsUsable => Width > 0 && Height > 0;
}

/// <summary>Editable guides for one debug frame, all relative to its top-left pixel.</summary>
public sealed record InGamePriceDebugGeometry(
    InGamePriceDebugRectangle? Template,
    InGamePriceDebugRectangle? Ocr,
    InGamePriceDebugRectangle? Overlay,
    InGamePriceDebugRectangle? OverlayText);

/// <summary>
/// A memory-only annotated screen frame used by the OCR calibration page.
/// The bitmap and geometry are relative to the captured image, so the UI can
/// render handles and convert mouse drags without touching desktop coordinates.
/// </summary>
public sealed record InGamePriceDebugFrame(
    byte[] PngBytes,
    int Width,
    int Height,
    InGamePriceDebugGeometry? Geometry = null);

public sealed record InGamePriceMatch(
    FeatureMarketItem Item,
    string MatchedName,
    double Similarity,
    bool IsExact = false);
