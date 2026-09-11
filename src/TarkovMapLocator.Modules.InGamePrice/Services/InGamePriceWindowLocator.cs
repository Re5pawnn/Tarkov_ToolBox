using System;
using CvRect = OpenCvSharp.Rect;

namespace TarkovMapLocator.Modules.InGamePrice.Services;

/// <summary>
/// Pure geometry/text rules that decide whether an item-details window is open
/// and where its title line must be, keyed off the weight line ("0.010 kg")
/// every EFT inspect window shows near its top-right corner.  Stash grids and
/// bare hover labels have no weight line, so they can never trigger pricing.
/// Kept free of engine/service dependencies so tests can drive it directly.
/// </summary>
public static class InGamePriceWindowLocator
{
    /// <summary>Reference weight-line text height at 100% UI scale, in pixels.</summary>
    public const double ReferenceLineHeight = 22.0;

    /// <summary>
    /// True when a recognized line reads like the inspect window's weight row:
    /// ends with "kg", carries at least one digit and stays short.  The scale
    /// icon in front of the number may OCR into a stray leading character,
    /// which is why only the tail is anchored.
    /// </summary>
    public static bool LooksLikeWeightLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var stripped = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character)) stripped.Append(char.ToLowerInvariant(character));
        }

        var value = stripped.ToString();
        if (value.Length is < 3 or > 12) return false;
        if (!value.EndsWith("kg", StringComparison.Ordinal)) return false;
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character)) return true;
        }
        return false;
    }

    public static double GetUiScale(CvRect weightBox) =>
        Math.Clamp(weightBox.Height / ReferenceLineHeight, 0.35, 2.25);

    /// <summary>
    /// The band, in the same coordinate space as <paramref name="weightBox"/>,
    /// that must contain the window title: it spans one window width to the
    /// left of the weight line and roughly two text rows above it.  The caller
    /// clamps the result to whatever surface it captures.
    /// </summary>
    public static CvRect GetTitleSearchBand(CvRect weightBox, double uiScale)
    {
        var left = weightBox.Right - (int)Math.Round(1000 * uiScale);
        var top = weightBox.Y - (int)Math.Round(100 * uiScale);
        var right = weightBox.Right + (int)Math.Round(24 * uiScale);
        var bottom = weightBox.Bottom + (int)Math.Round(8 * uiScale);
        return new CvRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// True when a detected line can be the window title for the given weight
    /// line (same coordinate space): it sits clearly above the weight row —
    /// which also skips the category breadcrumb sharing the weight's row — and
    /// has a comparable text height.
    /// </summary>
    public static bool IsTitleCandidate(CvRect box, CvRect weightBox)
    {
        if (box.Width < 40) return false;
        if (box.Bottom > weightBox.Y + weightBox.Height * 0.4) return false;
        var heightRatio = (double)box.Height / Math.Max(1, weightBox.Height);
        return heightRatio is >= 0.6 and <= 2.4;
    }
}
