using System.IO;
using System.Windows;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.TaskTracking.Controls;
using TarkovMapLocator.Modules.TaskTracking.Services;

namespace TarkovMapLocator.Modules.TaskTracking;

public sealed class TaskTrackingFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.TaskTracking,
        "任务追踪",
        "任务",
        60);

    public FrameworkElement CreateView(IFeatureHost host, string route) => new TaskTrackingView(host);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.TaskTracking, StringComparison.Ordinal))
            throw new InvalidOperationException("任务追踪组件路由异常。");

        var result = TaskTrackingCatalogService.Load();
        if (!result.IsAvailable || result.Catalog is null)
            throw new InvalidDataException(result.ErrorMessage ?? "任务追踪组件无法读取任务树数据。");
        var catalog = result.Catalog;
        var modes = catalog.Tasks.Select(task => task.Mode).ToHashSet(StringComparer.Ordinal);
        if (catalog.Tasks.Count < 1_000 || !modes.SetEquals(["pvp", "pve", "season"]))
            throw new InvalidDataException("任务追踪组件的 PVP、PVE 或赛季服任务数据不完整。");

        var taskIds = catalog.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var danglingRelations = catalog.Tasks
            .SelectMany(task => task.PreviousTaskIds.Concat(task.NextTaskIds))
            .Where(id => !taskIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
        if (danglingRelations.Length > 0)
            throw new InvalidDataException($"任务追踪组件存在失效前后置关系：{string.Join("、", danglingRelations)}");

        var prerequisites = TaskPrerequisiteCatalogService.Load();
        var danglingPrerequisites = prerequisites
            .Where(pair => !taskIds.Contains(pair.Key) || pair.Value.Any(id => !taskIds.Contains(id)))
            .Select(pair => pair.Key)
            .Take(10)
            .ToArray();
        if (prerequisites.Count < 300 || danglingPrerequisites.Length > 0)
            throw new InvalidDataException($"任务追踪组件的前置关系数据不完整：{string.Join("、", danglingPrerequisites)}");
    }
}
