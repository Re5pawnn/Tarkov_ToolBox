using System.IO;
using System.Text.Json;
using TarkovMapLocator.Modules.TaskTracking;
using TarkovMapLocator.Modules.TaskTracking.Models;

namespace TarkovMapLocator.Modules.TaskTracking.Services;

public static class TaskTrackingCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TaskTrackingCatalogLoadResult Load()
    {
        var path = Path.Combine(TaskTrackingRuntime.ModuleDirectory, "task-data", "task-tracking-catalog.json");
        try
        {
            if (!File.Exists(path))
                return new TaskTrackingCatalogLoadResult(null, "缺少任务树数据文件。");

            var catalog = JsonSerializer.Deserialize<TaskTrackingCatalog>(File.ReadAllText(path), JsonOptions);
            if (catalog is not { SchemaVersion: 1, Tasks.Count: > 0 })
                return new TaskTrackingCatalogLoadResult(null, "任务树数据格式无效。");

            return new TaskTrackingCatalogLoadResult(catalog, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            TaskTrackingRuntime.Warning("读取任务树数据失败", exception.ToString());
            return new TaskTrackingCatalogLoadResult(null, "任务树数据读取失败。");
        }
    }
}
