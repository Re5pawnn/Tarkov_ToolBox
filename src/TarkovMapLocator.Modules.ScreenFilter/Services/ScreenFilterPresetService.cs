using System.IO;
using System.Text.Json;
using TarkovMapLocator.Modules.ScreenFilter.Models;

namespace TarkovMapLocator.Modules.ScreenFilter.Services;

public static class ScreenFilterPresetService
{
    private const int SchemaVersion = 1;
    private const int MaximumPresetCount = 64;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string PresetPath = Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "screen-filter-presets.json");

    public static IReadOnlyList<ScreenFilterSavedPreset> Load()
    {
        try
        {
            if (!File.Exists(PresetPath)) return [];
            return Parse(File.ReadAllText(PresetPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("滤镜", "读取自定义滤镜配置失败", exception.ToString());
            return [];
        }
    }

    public static bool Save(IEnumerable<ScreenFilterSavedPreset>? presets)
    {
        string? temporaryPath = null;
        try
        {
            var normalized = Normalize(presets).ToArray();
            var directory = Path.GetDirectoryName(PresetPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = $"{PresetPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new PresetFile(SchemaVersion, normalized), JsonOptions));
            File.Move(temporaryPath, PresetPath, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Warning("滤镜", "保存自定义滤镜配置失败", exception.ToString());
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogService.Warning("滤镜", "清理自定义滤镜配置临时文件失败", exception.Message);
                }
            }
        }
    }

    internal static IReadOnlyList<ScreenFilterSavedPreset> ParseForTest(string? json) => Parse(json);

    private static IReadOnlyList<ScreenFilterSavedPreset> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var file = JsonSerializer.Deserialize<PresetFile>(json, JsonOptions);
            return file?.SchemaVersion == SchemaVersion ? Normalize(file.Presets).ToArray() : [];
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            RuntimeLogService.Warning("滤镜", "自定义滤镜配置格式无效", exception.Message);
            return [];
        }
    }

    private static IEnumerable<ScreenFilterSavedPreset> Normalize(IEnumerable<ScreenFilterSavedPreset>? presets) =>
        (presets ?? [])
            .Where(preset => preset is not null)
            .Select(preset => preset.Normalize())
            .GroupBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(MaximumPresetCount);

    private sealed record PresetFile(int SchemaVersion, ScreenFilterSavedPreset[] Presets);
}
