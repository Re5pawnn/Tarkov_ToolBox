using System.Windows.Media;

namespace TarkovMapLocatorDesktop.Models;

// Map definitions are UI identity objects. Keep reference equality here: the
// lazily-loaded ImageSource changes during map switches, so record/value
// equality would change while WPF's Selector is still tracking the item.
public sealed class MapDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public required string ImageFileName { get; init; }
    public required string Players { get; init; }
    public required string Extracts { get; init; }
    public required string Duration { get; init; }
    public required string RaidStatsNote { get; init; }
    public string? ImageFilePath { get; internal set; }
    public ImageSource? ThumbnailSource { get; internal set; }
    public ImageSource? ImageSource { get; internal set; }
    public IReadOnlyList<MapMarker> Markers { get; internal set; } = [];
    public MapCoordinateBounds? WorldBounds { get; internal set; }
}

public sealed class MapMarker
{
    public required string Type { get; init; }
    public required string Label { get; init; }
    public string? Faction { get; init; }
    public string? ToolTipText { get; init; }
    public string? PreviewImageId { get; init; }
    public string? PreviewImageUrl { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public bool ShowLabel { get; init; }
    public bool VisibleOnSurface { get; init; }
    public double? HeadingDegrees { get; init; }
    public string? ColorHex { get; init; }
    public double? WorldX { get; init; }
    public double? WorldHeight { get; init; }
    public double? WorldZ { get; init; }
}

public readonly record struct MapPointProjection(
    double Xx,
    double Xy,
    double XOffset,
    double Yx,
    double Yy,
    double YOffset)
{
    public static MapPointProjection Identity { get; } = new(1, 0, 0, 0, 1, 0);

    public (double X, double Y) Transform(double x, double y) =>
        (Xx * x + Xy * y + XOffset, Yx * x + Yy * y + YOffset);
}

public readonly record struct MapCoordinateBounds(
    double X0,
    double Z0,
    double X1,
    double Z1,
    bool ReverseCoordinate,
    double CoordinateRotation,
    double PositionRotation = 0)
{
    private const double EdgeTolerance = .08;

    public bool Contains(double x, double z)
    {
        var width = X1 - X0;
        var height = Z1 - Z0;
        if (Math.Abs(width) < double.Epsilon || Math.Abs(height) < double.Epsilon) return false;

        var u = (x - X0) / width;
        var v = (z - Z0) / height;
        if (ReverseCoordinate) (u, v) = (v, u);
        RotateScreenVector(ref u, ref v, PositionRotation, .5);
        return double.IsFinite(u) && double.IsFinite(v) && u is >= 0 and <= 1 && v is >= 0 and <= 1;
    }

    public bool TryProject(double x, double z, out double u, out double v)
    {
        u = v = 0;
        var width = X1 - X0;
        var height = Z1 - Z0;
        if (Math.Abs(width) < double.Epsilon || Math.Abs(height) < double.Epsilon) return false;

        u = (x - X0) / width;
        v = (z - Z0) / height;
        if (ReverseCoordinate) (u, v) = (v, u);
        RotateScreenVector(ref u, ref v, PositionRotation, .5);
        if (!double.IsFinite(u) || !double.IsFinite(v) || u is < -EdgeTolerance or > 1 + EdgeTolerance || v is < -EdgeTolerance or > 1 + EdgeTolerance)
            return false;

        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        return true;
    }

    public double ProjectHeading(double headingDegrees)
    {
        var radians = headingDegrees * Math.PI / 180d;
        if (!TryProjectDirection(Math.Sin(radians), Math.Cos(radians), out var screenX, out var screenY))
            return 0;

        // Marker arrows point up at 0 degrees and WPF rotates clockwise. Deriving
        // the direction from the exact same projection as the position keeps maps
        // with quarter-turns or reversed axes correct as well.
        return NormalizeDegrees(Math.Atan2(screenX, -screenY) * 180d / Math.PI);
    }

    public bool TryProjectDirection(double worldX, double worldZ, out double screenX, out double screenY)
    {
        screenX = screenY = 0;
        var width = X1 - X0;
        var height = Z1 - Z0;
        if (Math.Abs(width) < double.Epsilon || Math.Abs(height) < double.Epsilon) return false;

        screenX = worldX / width;
        screenY = worldZ / height;
        if (ReverseCoordinate) (screenX, screenY) = (screenY, screenX);
        RotateScreenVector(ref screenX, ref screenY, PositionRotation, 0);
        return double.IsFinite(screenX) && double.IsFinite(screenY);
    }

    private static void RotateScreenVector(ref double x, ref double y, double degrees, double origin)
    {
        var normalized = NormalizeDegrees(degrees);
        if (Math.Abs(normalized) < double.Epsilon) return;

        var radians = normalized * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var offsetX = x - origin;
        var offsetY = y - origin;
        x = origin + offsetX * cosine - offsetY * sine;
        y = origin + offsetX * sine + offsetY * cosine;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }
}
