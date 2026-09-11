using System.IO;
using System.Reflection;
using System.Text.Json;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

internal static class MapCatalogService
{
    private const string FileName = "map-catalog.json";
    private const string EmbeddedResourceName = "TarkovMapLocatorDesktop.Resources.MapCatalog.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<MapCatalogLoadResult> Cached = new(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static MapCatalogLoadResult Load() => Cached.Value;

    private static MapCatalogLoadResult LoadCore()
    {
        Exception? externalFailure = null;
        var externalPath = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(externalPath))
        {
            try
            {
                using var stream = File.OpenRead(externalPath);
                return Parse(stream, externalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                externalFailure = exception;
                RuntimeLogService.Warning("地图资料", "外部地图资料无效，改用程序内置副本", $"文件: {externalPath}\n{exception}");
            }
        }

        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
                             ?? throw new FileNotFoundException("程序内置地图资料缺失。", EmbeddedResourceName);
        try
        {
            return Parse(embedded, EmbeddedResourceName);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException("外部与内置地图资料均无法读取。", externalFailure ?? exception);
        }
    }

    private static MapCatalogLoadResult Parse(Stream stream, string origin)
    {
        var file = JsonSerializer.Deserialize<MapCatalogFile>(stream, JsonOptions)
                   ?? throw new InvalidDataException("地图资料为空。");
        if (file.SchemaVersion != 1 || file.Maps.Length == 0)
            throw new InvalidDataException($"不支持的地图资料版本：{file.SchemaVersion}");
        if (file.Maps.Any(map => string.IsNullOrWhiteSpace(map.Id) || string.IsNullOrWhiteSpace(map.Name) || string.IsNullOrWhiteSpace(map.Image)))
            throw new InvalidDataException("地图资料包含缺少编号、名称或图片的条目。");
        var duplicate = file.Maps.GroupBy(map => map.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"地图资料包含重复编号：{duplicate.Key}");

        var checkedAt = file.SourceCheckedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var definitions = file.Maps.Select(map => new MapDefinition
        {
            Id = map.Id,
            Name = map.Name,
            Subtitle = map.Subtitle,
            ImageFileName = map.Image,
            Players = map.Players,
            Extracts = map.Extracts,
            Duration = map.Duration,
            RaidStatsNote = $"数据来源：Escape from Tarkov Wiki（核对于 {checkedAt}）。" +
                            (string.IsNullOrWhiteSpace(map.Note) ? "" : $"{map.Note}") +
                            "撤离 / 转移为资料表中的全部点位数，实际可用出口取决于阵营、出生点、条件与当前战局。"
        }).ToArray();
        return new MapCatalogLoadResult(file.SchemaVersion, file.DataVersion, file.Source, file.SourceCheckedAt, origin, definitions);
    }

    private sealed class MapCatalogFile
    {
        public int SchemaVersion { get; set; }
        public string DataVersion { get; set; } = "";
        public string Source { get; set; } = "";
        public DateOnly SourceCheckedAt { get; set; }
        public MapCatalogEntry[] Maps { get; set; } = [];
    }

    private sealed class MapCatalogEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Image { get; set; } = "";
        public string Players { get; set; } = "";
        public string Extracts { get; set; } = "";
        public string Duration { get; set; } = "";
        public string? Note { get; set; }
    }
}

internal sealed record MapCatalogLoadResult(
    int SchemaVersion,
    string DataVersion,
    string Source,
    DateOnly SourceCheckedAt,
    string Origin,
    IReadOnlyList<MapDefinition> Maps);
