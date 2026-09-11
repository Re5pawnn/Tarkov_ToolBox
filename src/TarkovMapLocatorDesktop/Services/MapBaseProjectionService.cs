using System.IO;
using System.Text.Json;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

public static class MapBaseProjectionService
{
    private const string SatelliteMapStyle = "satellite-map";
    private static readonly Lazy<IReadOnlyDictionary<string, WebMapDefinition>> WebMaps =
        new(LoadWebMaps, LazyThreadSafetyMode.ExecutionAndPublication);

    public static MapPointProjection Resolve(string? mapId, string? style) => MapPointProjection.Identity;

    public static bool HasWebMap(string? mapId) =>
        !string.IsNullOrWhiteSpace(mapId) && WebMaps.Value.ContainsKey(mapId);

    public static bool TryGetWebMap(string? mapId, out WebMapDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(mapId) && WebMaps.Value.TryGetValue(mapId, out var found))
        {
            definition = found;
            return true;
        }

        definition = WebMapDefinition.Empty;
        return false;
    }

    public static (double X, double Y) ProjectMarker(
        MapMarker marker,
        string? mapId,
        string? style,
        MapPointProjection fallbackProjection)
    {
        if (IsSatelliteMapStyle(style) &&
            TryGetWebMap(mapId, out var definition) &&
            marker.WorldX is { } worldX && double.IsFinite(worldX) &&
            marker.WorldZ is { } worldZ && double.IsFinite(worldZ))
            return ProjectWebWorld(definition, worldX, worldZ);

        return fallbackProjection.Transform(marker.X, marker.Y);
    }

    public static (double X, double Y) ProjectWebWorld(WebMapDefinition definition, double worldX, double worldZ)
    {
        var radians = definition.CoordinateRotation * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var rotatedX = worldX * cosine - worldZ * sine;
        var rotatedZ = worldX * sine + worldZ * cosine;
        var scale = Math.Pow(2, definition.Zoom);
        var globalPixelX = scale * (definition.Transform[0] * rotatedX + definition.Transform[1]);
        var globalPixelY = scale * (-definition.Transform[2] * rotatedZ + definition.Transform[3]);
        return (
            (globalPixelX - definition.Crop.X) / definition.Crop.Width,
            (globalPixelY - definition.Crop.Y) / definition.Crop.Height);
    }

    public static bool IsSatelliteMapStyle(string? style) =>
        string.Equals(style, SatelliteMapStyle, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(style, "web-map", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(style, "helper-test", StringComparison.OrdinalIgnoreCase);

    public static bool IsWebMapStyle(string? style) => IsSatelliteMapStyle(style);

    private static IReadOnlyDictionary<string, WebMapDefinition> LoadWebMaps()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "web-map-projections.json");
        if (!File.Exists(path)) return new Dictionary<string, WebMapDefinition>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<WebMapManifest>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return (manifest?.Maps ?? [])
                .Where(static map => map.IsValid)
                .GroupBy(static map => map.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            RuntimeLogService.Warning("卫星地图", "卫星地图配置读取失败", ex.ToString());
            return new Dictionary<string, WebMapDefinition>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public sealed record WebMapDefinition(
    string Id,
    string Name,
    string Image,
    string Mode,
    int Zoom,
    double[] Transform,
    double CoordinateRotation,
    double[][] Bounds,
    WebMapCrop Crop,
    int[] PixelSize,
    string? SurfaceName = null,
    WebMapLayerDefinition[]? Layers = null)
{
    public static WebMapDefinition Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, 0, [], 0, [], new(0, 0, 0, 0), [], null, []);

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Image) &&
        Transform is { Length: 4 } &&
        Crop.Width > 0 && Crop.Height > 0 &&
        PixelSize is { Length: 2 } && PixelSize.All(static value => value > 0);
}

public sealed record WebMapCrop(double X, double Y, double Width, double Height);

public sealed record WebMapLayerDefinition(
    string Id,
    string Name,
    string Image,
    int[] PixelSize,
    WebMapLayerExtentDefinition[]? Extents = null);

public sealed record WebMapLayerExtentDefinition(double[] Height, double[][][]? Bounds = null);

internal sealed record WebMapManifest(int SchemaVersion, string Source, string GeneratedAt, WebMapDefinition[] Maps);
