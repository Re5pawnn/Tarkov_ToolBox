using System.Windows;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.TaskItems;

public sealed class TaskItemsFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.TaskItems,
        "任务物品清单",
        "清单",
        50);

    public FrameworkElement CreateView(IFeatureHost host, string route) => new TaskItemsView(host);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.TaskItems, StringComparison.Ordinal))
            throw new InvalidOperationException("任务物品清单组件路由异常。");
    }
}
