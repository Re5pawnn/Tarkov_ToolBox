using System.Windows;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.TeamSync;

public sealed class TeamSyncFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.TeamSync,
        "队友位置共享",
        "队友",
        90);

    public FrameworkElement CreateView(IFeatureHost host, string route) => new TeamSyncView(host);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.TeamSync, StringComparison.Ordinal))
            throw new InvalidOperationException("队友共享组件路由异常。");

        var normalized = Models.LanSyncConfig.Default.Normalize();
        if (normalized.SyncPort != Models.LanSyncConfig.DefaultPort || normalized.Mode != "host")
            throw new InvalidOperationException("队友共享默认配置异常。");
    }
}
