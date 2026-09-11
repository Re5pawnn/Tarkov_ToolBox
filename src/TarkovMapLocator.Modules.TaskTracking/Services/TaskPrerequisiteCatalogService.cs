using System.IO;
using System.Text.Json;
using TarkovMapLocator.Modules.TaskTracking;

namespace TarkovMapLocator.Modules.TaskTracking.Services;

public static class TaskPrerequisiteCatalogService
{
    public static IReadOnlyDictionary<string, string[]> Load()
    {
        var path = GameTaskMapPath;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("prerequisites", out var prerequisites) ||
                prerequisites.ValueKind != JsonValueKind.Array)
                return new Dictionary<string, string[]>(StringComparer.Ordinal);

            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var entry in prerequisites.EnumerateArray())
            {
                var taskId = entry.TryGetProperty("trackingTaskId", out var taskIdProperty)
                    ? taskIdProperty.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(taskId) ||
                    !entry.TryGetProperty("prerequisiteTrackingTaskIds", out var ids) ||
                    ids.ValueKind != JsonValueKind.Array)
                    continue;

                var prerequisiteIds = ids.EnumerateArray()
                    .Select(property => property.GetString()?.Trim())
                    .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, taskId, StringComparison.Ordinal))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (prerequisiteIds.Length > 0)
                    result[taskId] = prerequisiteIds;
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TaskTrackingRuntime.Warning("读取任务前置关系失败", exception.Message);
            return new Dictionary<string, string[]>(StringComparer.Ordinal);
        }
    }

    public static IReadOnlyDictionary<string, string[]> LoadGameTaskMap()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(GameTaskMapPath));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !document.RootElement.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Array)
                throw new JsonException("任务编号映射格式无效");

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings.EnumerateArray())
            {
                var trackingId = mapping.TryGetProperty("trackingTaskId", out var trackingProperty)
                    ? trackingProperty.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(trackingId) ||
                    !mapping.TryGetProperty("gameTaskIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var idProperty in ids.EnumerateArray())
                {
                    var gameId = idProperty.GetString()?.Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    if (!result.TryGetValue(gameId, out var trackingIds)) result[gameId] = trackingIds = [];
                    if (!trackingIds.Contains(trackingId, StringComparer.Ordinal)) trackingIds.Add(trackingId);
                }
            }
            return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TaskTrackingRuntime.Warning("任务编号映射不可用，自动任务识别已跳过", exception.Message);
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GameTaskMapPath =>
        Path.Combine(TaskTrackingRuntime.ModuleDirectory, "task-data", "task-game-id-map.json");

    public static IReadOnlyCollection<string> Expand(
        IReadOnlyDictionary<string, string[]> prerequisitesByTaskId,
        IEnumerable<string> taskIds)
    {
        var roots = taskIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var taskId))
        {
            if (!visited.Add(taskId) || !prerequisitesByTaskId.TryGetValue(taskId, out var prerequisiteIds))
                continue;
            foreach (var prerequisiteId in prerequisiteIds)
            {
                if (result.Add(prerequisiteId)) pending.Push(prerequisiteId);
            }
        }
        result.ExceptWith(roots);
        return result;
    }
}
