using System.IO;
using System.Text.Json;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

public sealed record MapLayerCatalogLoadResult(
    IReadOnlyDictionary<string, IReadOnlyList<MapLayerDefinition>> LayersByMap,
    IReadOnlyDictionary<string, string> SurfaceNamesByMap,
    int AvailableLayerCount,
    int MissingAssetCount,
    string? ErrorMessage)
{
    public bool IsAvailable => ErrorMessage is null && AvailableLayerCount > 0;
}

public static class MapLayerCatalogService
{
    public static MapLayerCatalogLoadResult Load()
    {
        var manifestPath = FindFile("map-layers.json");
        var assetDirectory = FindAssetDirectory();
        if (manifestPath is null)
            return Empty("未找到 map-layers.json");
        if (assetDirectory is null)
            return Empty("未找到 assets/maps/layers 目录");

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("maps", out var mapsElement) ||
                mapsElement.ValueKind != JsonValueKind.Array)
                return Empty("map-layers.json 格式不正确");

            var layersByMap = new Dictionary<string, IReadOnlyList<MapLayerDefinition>>(StringComparer.OrdinalIgnoreCase);
            var surfaceNamesByMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var availableCount = 0;
            var missingCount = 0;
            foreach (var mapElement in mapsElement.EnumerateArray())
            {
                var mapId = ReadString(mapElement, "id");
                var surfaceName = ReadString(mapElement, "surfaceName");
                if (!string.IsNullOrWhiteSpace(mapId) && !string.IsNullOrWhiteSpace(surfaceName))
                    surfaceNamesByMap[mapId] = surfaceName;
                if (string.IsNullOrWhiteSpace(mapId) ||
                    !mapElement.TryGetProperty("layers", out var layersElement) ||
                    layersElement.ValueKind != JsonValueKind.Array)
                    continue;

                var mapLayers = new List<MapLayerDefinition>();
                foreach (var layerElement in layersElement.EnumerateArray())
                {
                    var id = ReadString(layerElement, "id");
                    var name = ReadString(layerElement, "name");
                    var imageFileName = ReadString(layerElement, "image");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(imageFileName)) continue;

                    var imagePath = Path.Combine(assetDirectory, imageFileName);
                    if (!File.Exists(imagePath))
                    {
                        missingCount++;
                        continue;
                    }

                    mapLayers.Add(new MapLayerDefinition
                    {
                        MapId = mapId,
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(name) ? id : name,
                        ImageFileName = imageFileName,
                        ImageFilePath = imagePath,
                        Extents = ReadExtents(layerElement)
                    });
                    availableCount++;
                }

                if (mapLayers.Count > 0) layersByMap[mapId] = mapLayers;
            }

            return new MapLayerCatalogLoadResult(layersByMap, surfaceNamesByMap, availableCount, missingCount, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty($"读取楼层地图失败：{exception.Message}");
        }
    }

    private static IReadOnlyList<MapLayerExtent> ReadExtents(JsonElement layerElement)
    {
        if (!layerElement.TryGetProperty("extents", out var extentsElement) ||
            extentsElement.ValueKind != JsonValueKind.Array)
            return [];

        var extents = new List<MapLayerExtent>();
        foreach (var extentElement in extentsElement.EnumerateArray())
        {
            var minimum = -10000d;
            var maximum = 10000d;
            if (extentElement.TryGetProperty("height", out var heightElement) &&
                heightElement.ValueKind == JsonValueKind.Array &&
                heightElement.GetArrayLength() >= 2)
            {
                if (heightElement[0].TryGetDouble(out var value)) minimum = value;
                if (heightElement[1].TryGetDouble(out value)) maximum = value;
            }

            var bounds = new List<MapLayerWorldBounds>();
            if (extentElement.TryGetProperty("bounds", out var boundsElement) &&
                boundsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var boundElement in boundsElement.EnumerateArray())
                {
                    if (boundElement.ValueKind != JsonValueKind.Array ||
                        boundElement.GetArrayLength() < 2 ||
                        !TryReadPair(boundElement[0], out var x0, out var z0) ||
                        !TryReadPair(boundElement[1], out var x1, out var z1))
                        continue;
                    bounds.Add(new MapLayerWorldBounds(x0, z0, x1, z1));
                }
            }

            extents.Add(new MapLayerExtent
            {
                MinimumHeight = minimum,
                MaximumHeight = maximum,
                Bounds = bounds
            });
        }
        return extents;
    }

    private static bool TryReadPair(JsonElement element, out double first, out double second)
    {
        first = second = 0;
        return element.ValueKind == JsonValueKind.Array &&
               element.GetArrayLength() >= 2 &&
               element[0].TryGetDouble(out first) &&
               element[1].TryGetDouble(out second);
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static MapLayerCatalogLoadResult Empty(string error) =>
        new(
            new Dictionary<string, IReadOnlyList<MapLayerDefinition>>(),
            new Dictionary<string, string>(),
            0,
            0,
            error);

    private static string? FindFile(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? FindAssetDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "assets", "maps", "layers");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
