using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.InGamePrice.Models;
using DrawingSize = System.Drawing.Size;
using CvRect = OpenCvSharp.Rect;

namespace TarkovMapLocator.Modules.InGamePrice.Services;

/// <summary>
/// Recognizes the item tooltip currently visible in the game.  Unlike the
/// standalone predecessor, frames never touch disk: screen capture, OpenCV and
/// PP-OCR exchange data in memory and the loop is deliberately throttled.
/// </summary>
public sealed class InGamePriceRecognitionService : IDisposable
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>Lines whose CTC confidence falls below this are discarded.</summary>
    private const double MinimumLineConfidence = 0.60;

    private readonly InGamePriceRecognitionSettings _settings;
    private readonly PaddleOcrEngine _paddleEngine;
    private bool _disposed;

    public InGamePriceRecognitionService(InGamePriceRecognitionSettings settings)
    {
        _settings = settings.Normalize();
        _paddleEngine = new PaddleOcrEngine(
            Path.Combine(InGamePriceModuleContext.ModuleDirectory, "assets", "in-game-price", "ppocr"));
    }

    public InGamePriceRecognitionSample Recognize(
        CancellationToken cancellationToken,
        bool includeDebugFrame = false)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        using var captured = CaptureScreen();
        return RecognizeAutomatically(captured, cancellationToken, includeDebugFrame);
    }

    /// <summary>
    /// Automatic mode, gated on an open item-details window: pricing fires only
    /// after the weight line ("0.010 kg") every inspect window shows near its
    /// top-right corner is found.  The window title is then read from the band
    /// left/above that weight line.  Bare stash grids and hover labels carry no
    /// weight line, so they can never trigger a price — this is what stops the
    /// tool from "pricing" random inventory cell captions.
    /// </summary>
    private InGamePriceRecognitionSample RecognizeAutomatically(
        CapturedBitmap captured,
        CancellationToken cancellationToken,
        bool includeDebugFrame)
    {
        var engine = _paddleEngine;
        using var bgr = DecodeBgr(captured.Bitmap);
        if (bgr.Empty())
            return CreateSample(false, string.Empty, 0, 0, 0, "截图解码失败", includeDebugFrame, captured.Bitmap);

        var boxes = engine.DetectTextBoxes(bgr, cancellationToken);
        if (boxes.Count == 0)
        {
            return CreateSample(false, string.Empty, 0, 0, 0, "等待物品详情窗口：请在游戏中打开物品检视界面", includeDebugFrame, captured.Bitmap);
        }

        var cursor = EftCaptureTargetService.GetSearchPoint(captured.Bounds);
        var cursorX = Math.Clamp(cursor.X - captured.Bounds.X, 0, captured.Bounds.Width);
        var cursorY = Math.Clamp(cursor.Y - captured.Bounds.Y, 0, captured.Bounds.Height);

        var weightBoxLocal = FindWeightLine(engine, bgr, boxes, cursorX, cursorY, cancellationToken);
        if (weightBoxLocal is not { } weightLocal)
        {
            return CreateSample(
                false,
                string.Empty,
                0,
                0,
                0,
                "等待物品详情窗口：未找到“重量 kg”标志行，仓库悬停不会触发识价",
                includeDebugFrame,
                captured.Bitmap);
        }

        var weightPhysical = new CvRect(
            captured.Bounds.X + weightLocal.X,
            captured.Bounds.Y + weightLocal.Y,
            weightLocal.Width,
            weightLocal.Height);
        var uiScale = InGamePriceWindowLocator.GetUiScale(weightPhysical);
        var band = InGamePriceWindowLocator.GetTitleSearchBand(weightPhysical, uiScale);

        // Read title candidates from the current frame when the band fits it,
        // otherwise grab exactly the missing band with a second small capture.
        List<InGamePriceOcrCandidate> candidates;
        var capturedRect = new CvRect(captured.Bounds.X, captured.Bounds.Y, captured.Bounds.Width, captured.Bounds.Height);
        if (capturedRect.Contains(band))
        {
            candidates = CollectTitleCandidates(engine, bgr, boxes, captured.Bounds, band, weightPhysical, cancellationToken);
        }
        else
        {
            var virtualScreen = GetVirtualScreenBounds();
            var virtualRect = new CvRect(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height);
            var clamped = band.Intersect(virtualRect);
            if (clamped.Width < 24 || clamped.Height < 12)
            {
                candidates = [];
            }
            else
            {
                var bandRegion = new ScreenCaptureRegion(clamped.X, clamped.Y, clamped.Width, clamped.Height);
                using var bandCapture = CaptureRegion(bandRegion);
                using var bandBgr = DecodeBgr(bandCapture.Bitmap);
                var bandBoxes = bandBgr.Empty()
                    ? Array.Empty<CvRect>()
                    : engine.DetectTextBoxes(bandBgr, cancellationToken);
                candidates = CollectTitleCandidates(engine, bandBgr, bandBoxes, bandCapture.Bounds, clamped, weightPhysical, cancellationToken);
            }
        }

        if (candidates.Count == 0)
        {
            return CreateSample(
                true,
                string.Empty,
                weightPhysical.X,
                weightPhysical.Y,
                0,
                "已定位物品窗口，但未能读到窗口标题文字",
                includeDebugFrame,
                captured.Bitmap,
                new Rectangle(weightLocal.X, weightLocal.Y, weightLocal.Width, weightLocal.Height),
                anchorScale: uiScale);
        }

        var primary = candidates[0];
        var titleLocal = new CvRect(
            primary.PhysicalX - captured.Bounds.X,
            primary.PhysicalY - captured.Bounds.Y,
            primary.Width,
            primary.Height);
        return CreateSample(
            true,
            primary.Text,
            primary.AnchorX,
            primary.AnchorY,
            primary.Confidence,
            null,
            includeDebugFrame,
            captured.Bitmap,
            new Rectangle(titleLocal.X, titleLocal.Y, titleLocal.Width, titleLocal.Height),
            weightLocal,
            primary.AnchorScale,
            candidates);
    }

    /// <summary>
    /// Looks for the inspect window's weight line among the short text boxes
    /// nearest the cursor. Stash captions ("40/40", item short names) simply
    /// fail the kg pattern and therefore cannot trigger a price lookup.
    /// </summary>
    private static CvRect? FindWeightLine(
        PaddleOcrEngine engine,
        Mat bgr,
        IReadOnlyList<CvRect> boxes,
        int cursorX,
        int cursorY,
        CancellationToken cancellationToken)
    {
        var narrow = boxes
            .Where(box => box.Height is >= 10 and <= 90 &&
                          box.Width is >= 36 and <= 480 &&
                          box.Width < box.Height * 14)
            .OrderBy(box =>
            {
                var dx = box.X + box.Width / 2.0 - cursorX;
                var dy = box.Y + box.Height / 2.0 - cursorY;
                return dx * dx + dy * dy;
            })
            .Take(12);
        foreach (var box in narrow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = engine.RecognizeLine(bgr, box, cancellationToken);
            if (line.Confidence >= 0.5 && InGamePriceWindowLocator.LooksLikeWeightLine(line.Text))
                return box;
        }

        return null;
    }

    /// <summary>
    /// Recognizes the few lines inside the title band that sit above the
    /// weight row, nearest-above first.  The category breadcrumb shares the
    /// weight's row and is skipped by the geometry filter; anything above the
    /// window would only be tried after the real title failed to read.
    /// </summary>
    private static List<InGamePriceOcrCandidate> CollectTitleCandidates(
        PaddleOcrEngine engine,
        Mat frame,
        IReadOnlyList<CvRect> boxes,
        ScreenCaptureRegion frameBounds,
        CvRect bandPhysical,
        CvRect weightPhysical,
        CancellationToken cancellationToken)
    {
        var candidates = new List<InGamePriceOcrCandidate>();
        if (frame.Empty()) return candidates;

        var bandLocal = new CvRect(
            bandPhysical.X - frameBounds.X,
            bandPhysical.Y - frameBounds.Y,
            bandPhysical.Width,
            bandPhysical.Height);
        var weightLocal = new CvRect(
            weightPhysical.X - frameBounds.X,
            weightPhysical.Y - frameBounds.Y,
            weightPhysical.Width,
            weightPhysical.Height);

        var titleBoxes = boxes
            .Where(box =>
            {
                var centerX = box.X + box.Width / 2;
                var centerY = box.Y + box.Height / 2;
                return centerX >= bandLocal.Left && centerX < bandLocal.Right &&
                       centerY >= bandLocal.Top && centerY < bandLocal.Bottom &&
                       InGamePriceWindowLocator.IsTitleCandidate(box, weightLocal);
            })
            .OrderByDescending(box => box.Y + box.Height)
            .Take(4);

        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var box in titleBoxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = engine.RecognizeLine(frame, box, cancellationToken);
            if (line.Confidence < MinimumLineConfidence) continue;
            var text = line.Text.Trim();
            var normalized = InGamePriceLookupIndex.Normalize(text);
            if (normalized.Length < 2 || !seenTexts.Add(normalized)) continue;
            candidates.Add(new InGamePriceOcrCandidate(
                text,
                line.Confidence,
                frameBounds.X + box.X,
                frameBounds.Y + box.Y,
                box.Width,
                box.Height));

            // The magnifier icon in front of the window title can merge into
            // the detected line and OCR as one stray leading glyph ("Q ULTRA
            // …").  Offer the stripped variant as an extra candidate — the raw
            // text stays first, so genuine names are never displaced.
            if (StripLeadingIconGlyph(text) is { } variant)
            {
                var variantNormalized = InGamePriceLookupIndex.Normalize(variant);
                if (variantNormalized.Length >= 2 && seenTexts.Add(variantNormalized))
                {
                    candidates.Add(new InGamePriceOcrCandidate(
                        variant,
                        line.Confidence,
                        frameBounds.X + box.X,
                        frameBounds.Y + box.Y,
                        box.Width,
                        box.Height));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Removes the first glyph — the typical OCR artifact of the magnifier
    /// icon merged into the title line ("PUHFRFID…" for "UHF RFID…").  CTC
    /// output often glues the stray glyph straight onto the text, so no space
    /// is required.  The variant is only an ADDITIONAL candidate: the raw text
    /// is always tried first, so genuine names are never displaced.  This also
    /// rescues the lookup index's first-character bucketing, which a corrupted
    /// leading glyph would otherwise send to the wrong bucket.
    /// </summary>
    private static string? StripLeadingIconGlyph(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length < 4) return null;
        var rest = trimmed[1..].TrimStart();
        return rest.Length >= 3 ? rest : null;
    }

    private InGamePriceRecognitionSample CreateSample(
        bool templateFound,
        string ocrText,
        int templateX,
        int templateY,
        double templateScore,
        string? issue,
        bool includeDebugFrame,
        Bitmap captured,
        Rectangle? templateBounds = null,
        CvRect? ocrBounds = null,
        double anchorScale = 1d,
        IReadOnlyList<InGamePriceOcrCandidate>? candidates = null)
    {
        var geometry = CreateDebugGeometry(templateBounds, ocrBounds, anchorScale);
        var debugFrame = includeDebugFrame
            ? CreateDebugFrame(captured, templateBounds, ocrBounds, anchorScale)
            : null;
        return new InGamePriceRecognitionSample(templateFound, ocrText, templateX, templateY, templateScore, issue, debugFrame, geometry, anchorScale, candidates);
    }

    private InGamePriceDebugGeometry CreateDebugGeometry(Rectangle? templateBounds, CvRect? ocrBounds, double anchorScale)
    {
        var templateGuide = templateBounds is { } template
            ? new InGamePriceDebugRectangle(template.X, template.Y, template.Width, template.Height)
            : null;
        var ocrGuide = ocrBounds is { } ocr
            ? new InGamePriceDebugRectangle(ocr.X, ocr.Y, ocr.Width, ocr.Height)
            : null;
        if (templateBounds is not { } templateBoundsValue)
            return new InGamePriceDebugGeometry(templateGuide, ocrGuide, null, null);

        var scale = Math.Clamp(anchorScale, 0.35, 2.25);
        var overlay = new InGamePriceDebugRectangle(
            templateBoundsValue.X + ScaleCoordinate(_settings.OverlayRelativeX, scale),
            templateBoundsValue.Y + ScaleCoordinate(_settings.OverlayRelativeY, scale),
            _settings.OverlayWidth,
            _settings.OverlayHeight);
        var text = new InGamePriceDebugRectangle(
            overlay.X + 17 + _settings.OverlayTextOffsetX,
            overlay.Y + 10 + _settings.OverlayTextOffsetY,
            Math.Max(80, Math.Min(overlay.Width - 26, 210)),
            Math.Max(16, (int)Math.Ceiling(_settings.OverlayFontSize * 1.35)) + 4);
        return new InGamePriceDebugGeometry(templateGuide, ocrGuide, overlay, text);
    }

    private InGamePriceDebugFrame CreateDebugFrame(
        Bitmap captured,
        Rectangle? templateBounds,
        CvRect? ocrBounds,
        double anchorScale)
    {
        using var annotated = (Bitmap)captured.Clone();
        using var graphics = Graphics.FromImage(annotated);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var templatePen = new Pen(Color.FromArgb(246, 91, 91), 2);
        using var ocrPen = new Pen(Color.FromArgb(78, 204, 126), 2);
        using var overlayPen = new Pen(Color.FromArgb(79, 169, 255), 2);
        using var textPen = new Pen(Color.FromArgb(255, 211, 77), 2);
        using var labelBrush = new SolidBrush(Color.White);
        using var font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold, GraphicsUnit.Pixel);
        InGamePriceDebugRectangle? overlayGuide = null;
        InGamePriceDebugRectangle? textGuide = null;
        var ocrGuide = ocrBounds is { } ocrGeometry
            ? new InGamePriceDebugRectangle(ocrGeometry.X, ocrGeometry.Y, ocrGeometry.Width, ocrGeometry.Height)
            : null;

        if (templateBounds is { } template)
        {
            DrawGuide(graphics, templatePen, labelBrush, font, template, "物品标题");

            var scale = Math.Clamp(anchorScale, 0.35, 2.25);
            var overlayBounds = new Rectangle(
                template.X + ScaleCoordinate(_settings.OverlayRelativeX, scale),
                template.Y + ScaleCoordinate(_settings.OverlayRelativeY, scale),
                _settings.OverlayWidth,
                _settings.OverlayHeight);
            overlayGuide = new InGamePriceDebugRectangle(overlayBounds.X, overlayBounds.Y, overlayBounds.Width, overlayBounds.Height);
            DrawGuide(graphics, overlayPen, labelBrush, font, overlayBounds, "价签");

            var textLineHeight = Math.Max(16, (int)Math.Ceiling(_settings.OverlayFontSize * 1.35));
            var textBounds = new Rectangle(
                overlayBounds.X + 17 + _settings.OverlayTextOffsetX,
                overlayBounds.Y + 10 + _settings.OverlayTextOffsetY,
                Math.Max(80, Math.Min(overlayBounds.Width - 26, 210)),
                textLineHeight + 4);
            textGuide = new InGamePriceDebugRectangle(textBounds.X, textBounds.Y, textBounds.Width, textBounds.Height);
            DrawGuide(graphics, textPen, labelBrush, font, textBounds, "文字");
        }

        if (ocrBounds is { } ocr)
            DrawGuide(graphics, ocrPen, labelBrush, font, new Rectangle(ocr.X, ocr.Y, ocr.Width, ocr.Height), "重量 kg");

        using var stream = new MemoryStream();
        annotated.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        var templateGuide = templateBounds is { } templateGeometry
            ? new InGamePriceDebugRectangle(templateGeometry.X, templateGeometry.Y, templateGeometry.Width, templateGeometry.Height)
            : null;
        return new InGamePriceDebugFrame(
            stream.ToArray(),
            annotated.Width,
            annotated.Height,
            new InGamePriceDebugGeometry(templateGuide, ocrGuide, overlayGuide, textGuide));
    }

    private static void DrawGuide(Graphics graphics, Pen pen, Brush labelBrush, Font font, Rectangle requestedBounds, string label)
    {
        var canvas = new Rectangle(0, 0, (int)graphics.VisibleClipBounds.Width, (int)graphics.VisibleClipBounds.Height);
        var bounds = Rectangle.Intersect(canvas, requestedBounds);
        if (bounds.Width < 1 || bounds.Height < 1) return;

        graphics.DrawRectangle(pen, bounds);
        var labelY = Math.Max(0, bounds.Y - font.Height - 3);
        using var labelBackground = new SolidBrush(Color.FromArgb(205, pen.Color));
        graphics.FillRectangle(labelBackground, bounds.X, labelY, Math.Max(28, label.Length * 18), font.Height + 3);
        graphics.DrawString(label, font, labelBrush, bounds.X + 3, labelY + 1);
    }

    private static int ScaleCoordinate(int value, double scale) => (int)Math.Round(value * scale);

    private static Mat DecodeBgr(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
        return Cv2.ImDecode(stream.ToArray(), ImreadModes.Color);
    }

    private static CapturedBitmap CaptureRegion(ScreenCaptureRegion bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new DrawingSize(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            return new CapturedBitmap(bitmap, bounds);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private CapturedBitmap CaptureScreen()
    {
        return CaptureRegion(GetAutomaticSearchBounds());
    }

    private static ScreenCaptureRegion GetVirtualScreenBounds()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        return new ScreenCaptureRegion(x, y, Math.Max(24, width), Math.Max(24, height));
    }

    private ScreenCaptureRegion GetAutomaticSearchBounds()
    {
        if (_settings.CaptureRegion is { IsUsable: true } configured)
            return configured.Normalize();

        var available = EftCaptureTargetService.TryGetWindowBounds() ?? GetVirtualScreenBounds();
        var width = Math.Min(_settings.AutoSearchWidth, available.Width);
        var height = Math.Min(_settings.AutoSearchHeight, available.Height);
        var point = EftCaptureTargetService.GetSearchPoint(available);
        var x = Math.Clamp(point.X - width / 2, available.X, available.X + available.Width - width);
        var y = Math.Clamp(point.Y - height / 2, available.Y, available.Y + available.Height - height);
        return new ScreenCaptureRegion(x, y, width, height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _paddleEngine.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InGamePriceRecognitionService));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private sealed class CapturedBitmap : IDisposable
    {
        public CapturedBitmap(Bitmap bitmap, ScreenCaptureRegion bounds)
        {
            Bitmap = bitmap;
            Bounds = bounds;
        }

        public Bitmap Bitmap { get; }
        public ScreenCaptureRegion Bounds { get; }
        public void Dispose() => Bitmap.Dispose();
    }
}

/// <summary>
/// Immutable alias index for a market snapshot. It keeps OCR matching away from
/// the thousands of visible list rows and only falls back to fuzzy comparison on
/// a small length/initial-character candidate set.
/// </summary>
public sealed class InGamePriceLookupIndex
{
    private sealed record Alias(string Normalized, string DisplayName, FeatureMarketItem Item, bool IsCanonicalName);

    private readonly Dictionary<string, Alias> _uniqueExact = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousExact = new(StringComparer.Ordinal);
    private readonly Dictionary<char, List<Alias>> _byInitial = [];
    private readonly IReadOnlyList<Alias> _aliases;

    public InGamePriceLookupIndex(IEnumerable<FeatureMarketItem> items)
    {
        var aliases = new List<Alias>();
        foreach (var item in items)
        {
            var itemAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (aliasText, isCanonicalName) in new[]
                     {
                         (item.NameZh, true),
                         (item.ShortNameZh, false),
                         (item.Name, true),
                         (item.ShortName, false)
                     })
            {
                var normalized = Normalize(aliasText);
                if (normalized.Length == 0 || !itemAliases.Add(normalized)) continue;
                var alias = new Alias(normalized, aliasText, item, isCanonicalName);
                AddExactAlias(alias);
                aliases.Add(alias);
                if (!_byInitial.TryGetValue(normalized[0], out var group))
                {
                    group = [];
                    _byInitial[normalized[0]] = group;
                }
                group.Add(alias);
            }
        }
        _aliases = aliases;
    }

    public InGamePriceMatch? FindBest(string text, double minimumSimilarity)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return null;
        if (_uniqueExact.TryGetValue(normalized, out var exact) &&
            (exact.IsCanonicalName || normalized.Length >= 3))
            return new InGamePriceMatch(exact.Item, exact.DisplayName, 1, IsExact: true);

        // Short partial OCR strings are not meaningful enough for fuzzy lookup.
        // In particular, text such as "re" used to be promoted to a Red keycard
        // by the old substring bonus.
        if (normalized.Length < 3 || _ambiguousExact.Contains(normalized)) return null;

        var candidates = _byInitial.TryGetValue(normalized[0], out var byInitial)
            ? byInitial
            : _aliases;
        Alias? best = null;
        var bestScore = 0d;
        var runnerUpScore = 0d;
        foreach (var candidate in candidates)
        {
            var lengthRatio = (double)Math.Min(normalized.Length, candidate.Normalized.Length) /
                              Math.Max(normalized.Length, candidate.Normalized.Length);
            if (lengthRatio < MinimumLengthRatio(normalized.Length)) continue;
            var score = Similarity(normalized, candidate.Normalized);
            if (best is null || score > bestScore)
            {
                if (best is not null && !string.Equals(best.Item.Id, candidate.Item.Id, StringComparison.Ordinal))
                    runnerUpScore = Math.Max(runnerUpScore, bestScore);
                bestScore = score;
                best = candidate;
            }
            else if (!string.Equals(best.Item.Id, candidate.Item.Id, StringComparison.Ordinal))
            {
                runnerUpScore = Math.Max(runnerUpScore, score);
            }
        }

        var requiredSimilarity = RequiredSimilarity(normalized.Length, minimumSimilarity);
        return best is not null && bestScore >= requiredSimilarity &&
               bestScore - runnerUpScore >= AmbiguityMargin(normalized.Length)
            ? new InGamePriceMatch(best.Item, best.DisplayName, bestScore, IsExact: false)
            : null;
    }

    private void AddExactAlias(Alias alias)
    {
        if (_ambiguousExact.Contains(alias.Normalized)) return;
        if (!_uniqueExact.TryGetValue(alias.Normalized, out var existing))
        {
            _uniqueExact[alias.Normalized] = alias;
            return;
        }

        if (string.Equals(existing.Item.Id, alias.Item.Id, StringComparison.Ordinal)) return;
        _uniqueExact.Remove(alias.Normalized);
        _ambiguousExact.Add(alias.Normalized);
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    private static double MinimumLengthRatio(int textLength) => textLength switch
    {
        3 => .80,
        4 => .72,
        _ => .62
    };

    private static double RequiredSimilarity(int textLength, double configuredMinimum)
    {
        var configured = Math.Clamp(configuredMinimum, .40, 1d);
        return textLength switch
        {
            3 => Math.Max(configured, .92),
            4 => Math.Max(configured, .87),
            _ => Math.Max(configured, .80)
        };
    }

    private static double AmbiguityMargin(int textLength) => textLength <= 4 ? .08 : .045;

    private static double Similarity(string first, string second)
    {
        if (first == second) return 1;
        var distance = Levenshtein(first, second);
        return 1d - (double)distance / Math.Max(first.Length, second.Length);
    }

    private static int Levenshtein(string first, string second)
    {
        if (first.Length == 0) return second.Length;
        if (second.Length == 0) return first.Length;
        if (first.Length < second.Length) (first, second) = (second, first);

        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var j = 0; j <= second.Length; j++) previous[j] = j;
        for (var i = 1; i <= first.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= second.Length; j++)
            {
                var cost = first[i - 1] == second[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[second.Length];
    }
}

/// <summary>
/// Exact aliases may be shown immediately.  A fuzzy result must remain stable
/// across two consecutive captures before it is allowed to drive the overlay.
/// </summary>
public sealed class InGamePriceMatchGate
{
    private string? _pendingItemId;
    private int _pendingCount;

    public InGamePriceMatch? Accept(InGamePriceMatch? candidate)
    {
        if (candidate is null)
        {
            Reset();
            return null;
        }

        if (candidate.IsExact)
        {
            Reset();
            return candidate;
        }

        if (string.Equals(_pendingItemId, candidate.Item.Id, StringComparison.Ordinal))
        {
            _pendingCount++;
        }
        else
        {
            _pendingItemId = candidate.Item.Id;
            _pendingCount = 1;
        }

        return _pendingCount >= 2 ? candidate : null;
    }

    public void Reset()
    {
        _pendingItemId = null;
        _pendingCount = 0;
    }
}

public static class InGamePriceOverlayFormatter
{
    public static string GetDisplayName(FeatureMarketItem item) =>
        FirstNotBlank(item.NameZh, item.ShortNameZh, item.Name, item.ShortName, "未知物品");

    public static string BuildText(InGamePriceMatch match)
    {
        var item = match.Item;
        var fleaAverage = item.FleaPrice ?? item.Avg24hPrice;
        var fleaMinimum = item.LastLowPrice ?? item.Low24hPrice;
        var trader = item.BestTrader;
        var lines = new List<string>
        {
            GetDisplayName(item),
            $"跳蚤均价：{FormatPrice(fleaAverage)}",
            $"跳蚤底价：{FormatPrice(fleaMinimum)}",
            trader is null
                ? "最高商人：—"
                : $"最高商人：{LocalizeTraderName(FirstNotBlank(trader.VendorName, "商人"))} {FormatPrice(trader.Price)}",
            $"单格价值：{FormatPrice(GetEffectivePerSlotPrice(item))}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Mirrors the original OCR tool's short Chinese trader names.  JSON API
    /// records normally provide the English name, while cached data can contain
    /// an already-localized value, which is intentionally preserved.
    /// </summary>
    public static string LocalizeTraderName(string? traderName)
    {
        var displayName = string.IsNullOrWhiteSpace(traderName) ? "商人" : traderName.Trim();
        return displayName.ToLowerInvariant() switch
        {
            "prapor" => "俄商",
            "therapist" => "大妈",
            "fence" => "黑商",
            "skier" => "滑雪商",
            "peacekeeper" => "美商",
            "mechanic" => "机械师",
            "ragman" => "服装商",
            "jaeger" => "猎人",
            "ref" => "竞技场裁判",
            _ => displayName
        };
    }

    public static int GetEffectivePerSlotPrice(FeatureMarketItem item)
    {
        var slots = Math.Max(1, (item.Width ?? 1) * (item.Height ?? 1));
        var best = item.LastLowPrice is > 0
            ? item.LastLowPrice
            : item.BestTrader?.Price is > 0
                ? item.BestTrader.Price
                : item.FleaPrice ?? item.Avg24hPrice;
        return best is > 0 ? (int)Math.Round(best.Value / (double)slots) : 0;
    }

    private static string FormatPrice(int? value) => value is > 0 ? $"{value:N0} ₽" : "—";

    private static string FirstNotBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
