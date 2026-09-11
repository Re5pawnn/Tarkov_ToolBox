using System.IO;
using System.Reflection;
using System.Text.Json;
using TarkovMapLocator.Modules.Utilities;

namespace TarkovMapLocator.Modules.Utilities.SeasonDocuments;

internal static class SeasonDocumentLocationService
{
    private const string ResourceName = "TarkovMapLocator.Modules.Utilities.Resources.SeasonDocumentLocations.json";
    private static readonly Lazy<SeasonDocumentLocationCatalog> Catalog = new(LoadCore);

    public static SeasonDocumentLocationCatalog Load() => Catalog.Value;

    public static void Verify()
    {
        var catalog = Load();
        var location = catalog.Documents.SelectMany(document => document.Locations).FirstOrDefault()
            ?? throw new InvalidDataException("赛季文档刷新位置数据为空。");
        if (!catalog.Documents.Any(document => Search(document.Id, location.MapLocalizedName, location.Title).Count > 0))
            throw new InvalidDataException("赛季文档刷新位置搜索验证失败。");
    }

    public static IReadOnlyList<SeasonDocumentLocation> Search(string documentId, string map, string query)
    {
        var document = Catalog.Value.Documents.FirstOrDefault(item => item.Id == documentId);
        if (document is null) return [];
        var term = query.Trim();
        return document.Locations.Where(location =>
                (map.Length == 0 || location.MapLocalizedName == map) &&
                (term.Length == 0 || location.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 location.MapLocalizedName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 location.Keys.Any(key => key.LocalizedName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                          key.Name.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    private static SeasonDocumentLocationCatalog LoadCore()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("赛季文档刷新位置数据未打包。");
        var catalog = JsonSerializer.Deserialize<SeasonDocumentLocationCatalog>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("赛季文档刷新位置数据无法读取。");
        if (!IsValidCatalog(catalog))
            throw new InvalidDataException("赛季文档刷新位置数据不完整。");
        return catalog;
    }

    internal static bool IsValidCatalogForTest(SeasonDocumentLocationCatalog catalog) => IsValidCatalog(catalog);

    private static bool IsValidCatalog(SeasonDocumentLocationCatalog catalog)
    {
        if (catalog.Documents.Count == 0 ||
            catalog.Documents.Any(document => string.IsNullOrWhiteSpace(document.Id)) ||
            catalog.Documents.Select(document => document.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Documents.Count)
            return false;

        return catalog.Documents.All(document =>
        {
            if (string.IsNullOrWhiteSpace(document.LocalizedName) || document.LocalizedName.Contains('\uFFFD') ||
                document.Maps.Count == 0 || document.Locations.Count == 0)
                return false;
            var mapNames = document.Maps
                .Where(map => !string.IsNullOrWhiteSpace(map.LocalizedName))
                .Select(map => map.LocalizedName)
                .ToHashSet(StringComparer.Ordinal);
            if (mapNames.Count != document.Maps.Count) return false;
            return document.Locations.All(location =>
                !string.IsNullOrWhiteSpace(location.Title) &&
                !location.Title.Contains('\uFFFD') &&
                mapNames.Contains(location.MapLocalizedName) &&
                UtilityImageReference.IsSupported(location.ImageCacheId, location.ImageUrl));
        });
    }
}

internal sealed class SeasonDocumentLocationCatalog
{
    public string SourceUrl { get; set; } = "";
    public DateTimeOffset SourceCheckedAt { get; set; }
    public List<SeasonDocumentDefinition> Documents { get; set; } = [];
}

internal sealed class SeasonDocumentDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LocalizedName { get; set; } = "";
    public string IconLink { get; set; } = "";
    public List<SeasonDocumentMap> Maps { get; set; } = [];
    public List<SeasonDocumentLocation> Locations { get; set; } = [];
}

internal sealed class SeasonDocumentMap
{
    public string Name { get; set; } = "";
    public string LocalizedName { get; set; } = "";
}

internal sealed class SeasonDocumentLocation
{
    public string Map { get; set; } = "";
    public string MapLocalizedName { get; set; } = "";
    public string Title { get; set; } = "";
    public string ImageCacheId { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public List<SeasonDocumentKey> Keys { get; set; } = [];
    public bool HasKeys => Keys.Count > 0;
}

internal sealed class SeasonDocumentKey
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LocalizedName { get; set; } = "";
}
