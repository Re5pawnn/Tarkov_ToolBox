using System.Windows;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Market;

public sealed class MarketFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.Market,
        "市场",
        "市场",
        20);

    public FrameworkElement CreateView(IFeatureHost host, string route) => new MarketView(host);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.Market, StringComparison.Ordinal))
            throw new InvalidOperationException("市场组件路由异常。");
    }
}
