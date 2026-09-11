using System.IO;
using System.Text.Json;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

public sealed record TaskPointLoadResult(
    IReadOnlyDictionary<string, IReadOnlyList<TaskMarkerGroup>> GroupsByMap,
    int TotalTaskCount,
    int TotalPointCount,
    string? ErrorMessage)
{
    public bool IsAvailable => ErrorMessage is null && GroupsByMap.Count > 0;
}

public static class TaskPointService
{
    public static TaskPointLoadResult Load()
    {
        var directory = FindTaskDataDirectory();
        if (directory is null)
            return new TaskPointLoadResult(new Dictionary<string, IReadOnlyList<TaskMarkerGroup>>(), 0, 0, "未找到 task-data 目录");

        try
        {
            var groupedMaps = new Dictionary<string, List<TaskMarkerGroup>>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in Directory.EnumerateFiles(directory, "*_task_markers.json", SearchOption.TopDirectoryOnly))
            {
                ReadTaskFile(filePath, groupedMaps);
            }

            var result = groupedMaps.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TaskMarkerGroup>)pair.Value.OrderBy(group => group.TaskName, StringComparer.CurrentCulture).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            return new TaskPointLoadResult(result, result.Values.Sum(groups => groups.Count), result.Values.Sum(groups => groups.Sum(group => group.Points.Count)), null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new TaskPointLoadResult(new Dictionary<string, IReadOnlyList<TaskMarkerGroup>>(), 0, 0, $"读取任务点位失败：{exception.Message}");
        }
    }

    private static void ReadTaskFile(string filePath, IDictionary<string, List<TaskMarkerGroup>> groupedMaps)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        var root = document.RootElement;
        var mapKey = ReadString(root, "mapNormalizedName");
        if (string.IsNullOrWhiteSpace(mapKey) || !root.TryGetProperty("markers", out var markers) || markers.ValueKind != JsonValueKind.Array) return;

        var groups = new Dictionary<string, List<TaskWorldPoint>>(StringComparer.Ordinal);
        var objectives = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var marker in markers.EnumerateArray())
        {
            var taskName = ReadString(marker, "questName");
            if (string.IsNullOrWhiteSpace(taskName) || !TryReadWorldPosition(marker, out var x, out var y, out var z)) continue;

            if (!groups.TryGetValue(taskName, out var points))
            {
                points = [];
                groups[taskName] = points;
                objectives[taskName] = [];
            }

            var objective = ReadString(marker, "conditionDescription");
            points.Add(new TaskWorldPoint(x, y, z, objective));
            if (!string.IsNullOrWhiteSpace(objective) && !objectives[taskName].Contains(objective, StringComparer.Ordinal))
                objectives[taskName].Add(objective);
        }

        if (!groupedMaps.TryGetValue(mapKey, out var mapGroups))
        {
            mapGroups = [];
            groupedMaps[mapKey] = mapGroups;
        }

        foreach (var group in groups)
        {
            mapGroups.Add(new TaskMarkerGroup(mapKey, group.Key, group.Value, objectives[group.Key]));
        }
    }

    private static bool TryReadWorldPosition(JsonElement marker, out double x, out double y, out double z)
    {
        x = y = z = 0;
        // Keep the same projection convention as the original desktop application: position.x / position.z.
        // This is the axis pair that matches the bounds in maps_detail.json (especially indoor maps).
        return marker.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object &&
               TryReadNumber(position, "x", out x) &&
               TryReadNumber(position, "y", out y) &&
               TryReadNumber(position, "z", out z);
    }

    private static string ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()?.Trim() ?? ""
            : "";

    private static bool TryReadNumber(JsonElement value, string name, out double number)
    {
        number = 0;
        return value.TryGetProperty(name, out var item) && item.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static string? FindTaskDataDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "task-data");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
