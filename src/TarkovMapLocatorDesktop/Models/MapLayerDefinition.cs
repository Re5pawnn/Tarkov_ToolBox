namespace TarkovMapLocatorDesktop.Models;

public sealed class MapLayerDefinition
{
    public required string MapId { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ImageFileName { get; init; }
    public required string ImageFilePath { get; init; }
    public IReadOnlyList<MapLayerExtent> Extents { get; init; } = [];

    public bool MatchesPosition(double worldX, double worldHeight, double worldZ)
    {
        if (!double.IsFinite(worldX) || !double.IsFinite(worldHeight) || !double.IsFinite(worldZ))
            return false;

        return Extents.Any(extent => extent.Matches(worldX, worldHeight, worldZ));
    }

    public bool MatchesMarker(MapMarker marker)
    {
        if (marker.WorldHeight is not { } height || !double.IsFinite(height)) return false;

        // Older hand-calibrated points may only have a floor height. They can
        // still be matched against a global height range, but a localized
        // building extent requires its original world X/Z coordinates.
        foreach (var extent in Extents)
        {
            if (!extent.ContainsHeight(height)) continue;
            if (extent.Bounds.Count == 0) return true;
            if (marker.WorldX is { } x && marker.WorldZ is { } z &&
                extent.Bounds.Any(bounds => bounds.Contains(x, z)))
                return true;
        }
        return false;
    }
}

public sealed class MapLayerExtent
{
    public required double MinimumHeight { get; init; }
    public required double MaximumHeight { get; init; }
    public IReadOnlyList<MapLayerWorldBounds> Bounds { get; init; } = [];

    public bool ContainsHeight(double height) =>
        height >= Math.Min(MinimumHeight, MaximumHeight) &&
        height <= Math.Max(MinimumHeight, MaximumHeight);

    public bool Matches(double worldX, double worldHeight, double worldZ) =>
        ContainsHeight(worldHeight) &&
        (Bounds.Count == 0 || Bounds.Any(bounds => bounds.Contains(worldX, worldZ)));
}

public readonly record struct MapLayerWorldBounds(double X0, double Z0, double X1, double Z1)
{
    public bool Contains(double worldX, double worldZ) =>
        worldX >= Math.Min(X0, X1) &&
        worldX <= Math.Max(X0, X1) &&
        worldZ >= Math.Min(Z0, Z1) &&
        worldZ <= Math.Max(Z0, Z1);
}

public sealed record MapLayerChoice(string Id, string Name, MapLayerDefinition? Layer);
