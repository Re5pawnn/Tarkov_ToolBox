namespace TarkovMapLocator.Core.Maps;

public enum MapPoiSource
{
    Extracts,
    Labels,
    More
}

public sealed record MapBounds(
    double X0,
    double Z0,
    double X1,
    double Z1,
    bool ReverseCoordinate)
{
    public double Area => Math.Abs((X1 - X0) * (Z1 - Z0));
}

public sealed record MapUnitPoint(double U, double V);

public sealed record MapWorldPoint(double X, double Z);

public sealed record MapHeightRange(double MinY, double MaxY);

public sealed record MapLayerBounds(
    double X0,
    double Z0,
    double X1,
    double Z1,
    string Label);

public sealed record MapLayerExtent(
    MapHeightRange? Height,
    IReadOnlyList<MapLayerBounds> Bounds);

public sealed record MapLayerMetadata(
    string Name,
    string SvgLayer,
    string TilePath,
    bool Show,
    IReadOnlyList<MapLayerExtent> Extents);

public record MapDisplayPoint(
    string Label,
    double U,
    double V,
    string Kind,
    double YawDegrees = 0);

public sealed record MapMetadata(
    string Id,
    string Key,
    string Name,
    string NormalizedName,
    string NameId,
    MapBounds Bounds,
    string SvgPath,
    string TilePath,
    MapHeightRange? HeightRange,
    IReadOnlyList<MapLayerMetadata> Layers,
    IReadOnlyList<MapPoi> Pois,
    IReadOnlyList<string> PoiSources)
{
    public double Area => Bounds.Area;
}

public sealed record MapPoi(
    string Label,
    double X,
    double Z,
    string Kind,
    string Source,
    string IconName = "",
    bool ShowLabel = false,
    string LabelText = "");

public sealed record ScreenshotCoordinate(
    string FileName,
    double X,
    double Y,
    double Z,
    double Qx,
    double Qy,
    double Qz,
    double Qw,
    double YawDegrees,
    long Order,
    long ModifiedAt);

public static class MapProjection
{
    public static bool Contains(MapBounds bounds, double x, double z)
    {
        var minX = Math.Min(bounds.X0, bounds.X1);
        var maxX = Math.Max(bounds.X0, bounds.X1);
        var minZ = Math.Min(bounds.Z0, bounds.Z1);
        var maxZ = Math.Max(bounds.Z0, bounds.Z1);
        return x >= minX && x <= maxX && z >= minZ && z <= maxZ;
    }

    public static MapUnitPoint ProjectWorldToUnit(MapBounds bounds, double x, double z)
    {
        var nx = (x - bounds.X0) / (bounds.X1 - bounds.X0);
        var nz = (z - bounds.Z0) / (bounds.Z1 - bounds.Z0);
        return bounds.ReverseCoordinate ? new MapUnitPoint(nz, nx) : new MapUnitPoint(nx, nz);
    }

    public static bool IsUnitRange(double value)
    {
        return double.IsFinite(value) && value >= 0 && value <= 1;
    }
}

public static class MapPoiSources
{
    public const string Extracts = "extracts";
    public const string Labels = "labels";
    public const string More = "more";

    public static string Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, Labels, StringComparison.OrdinalIgnoreCase))
        {
            return Labels;
        }

        return string.Equals(text, More, StringComparison.OrdinalIgnoreCase) ? More : Extracts;
    }

    public static MapPoiSource Parse(string? value)
    {
        return Normalize(value) switch
        {
            Labels => MapPoiSource.Labels,
            More => MapPoiSource.More,
            _ => MapPoiSource.Extracts
        };
    }
}
