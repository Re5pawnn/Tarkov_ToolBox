using System.Text.Json;
using TarkovMapLocator.App.Models;
using TarkovMapLocator.Core.Maps;

namespace TarkovMapLocator.App.Services;

public sealed class MapMetadataService
{
    public const string ExtractsPoiSource = MapPoiSources.Extracts;
    public const string LabelsPoiSource = MapPoiSources.Labels;
    private static readonly string[] SupportedImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    private readonly string appDirectory;

    public MapMetadataService()
        : this(AppContext.BaseDirectory)
    {
    }

    public MapMetadataService(string appDirectory)
    {
        this.appDirectory = appDirectory;
    }

    public IReadOnlyList<MapPrototypeMetadata> LoadMaps()
    {
        var detailPath = Path.Combine(appDirectory, "maps_detail.json");
        if (!File.Exists(detailPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(detailPath));
            return MapMetadataParser.ParseMaps(document.RootElement)
                .Select(ToPrototypeMetadata)
                .OrderByDescending(map => string.Equals(map.Key, "customs", StringComparison.OrdinalIgnoreCase))
                .ThenBy(map => map.Name, StringComparer.CurrentCulture)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private MapPrototypeMetadata ToPrototypeMetadata(MapMetadata metadata)
    {
        return new MapPrototypeMetadata(
            metadata.Id,
            metadata.Key,
            metadata.Name,
            metadata.NormalizedName,
            metadata.NameId,
            metadata.Bounds.X0,
            metadata.Bounds.Z0,
            metadata.Bounds.X1,
            metadata.Bounds.Z1,
            metadata.Bounds.ReverseCoordinate,
            metadata.SvgPath,
            metadata.TilePath,
            ResolveAppAssetPath(metadata.SvgPath),
            ResolveLocalImagePath(metadata.Key, metadata.NormalizedName, metadata.NameId, metadata.SvgPath),
            metadata.HeightRange,
            metadata.Layers,
            metadata.Pois
                .Select(poi => new MapPrototypePoi(
                    poi.Label,
                    poi.X,
                    poi.Z,
                    poi.Kind,
                    poi.Source,
                    poi.IconName,
                    poi.ShowLabel,
                    poi.LabelText))
                .ToArray(),
            metadata.PoiSources);
    }

    private string? ResolveLocalImagePath(string key, string normalizedName, string nameId, string svgPath)
    {
        var mapsDirectory = Path.Combine(appDirectory, "assets", "maps");
        var bundledCacheDirectory = Path.Combine(mapsDirectory, "native-cache");
        var candidates = new List<string>();

        AddCandidate(candidates, Path.GetFileNameWithoutExtension(svgPath));
        AddCandidate(candidates, key);
        AddCandidate(candidates, normalizedName);
        AddCandidate(candidates, nameId);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var extension in SupportedImageExtensions)
            {
                var cachePath = Path.Combine(bundledCacheDirectory, candidate + extension);
                if (MapImageCacheService.IsUsableCachedImage(cachePath))
                {
                    return cachePath;
                }

                var path = Path.Combine(mapsDirectory, candidate + extension);
                if (File.Exists(path) && !IsTemporaryPrototypeRaster(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static void AddCandidate(List<string> candidates, string? value)
    {
        var text = value?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            candidates.Add(text);
        }
    }

    private static bool IsTemporaryPrototypeRaster(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("-temp", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("satellite", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveAppAssetPath(string relativePath)
    {
        var text = relativePath.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
        return Path.Combine(appDirectory, text);
    }

}
