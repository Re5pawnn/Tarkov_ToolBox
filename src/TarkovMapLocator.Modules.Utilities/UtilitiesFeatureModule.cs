using System.Windows;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Utilities;

public sealed class UtilitiesFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.Utilities,
        "小工具",
        "小工具",
        100);

    public FrameworkElement CreateView(IFeatureHost host, string route) =>
        new UtilitiesShellView(host, route);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.Utilities, StringComparison.Ordinal))
            throw new InvalidOperationException("小工具组件路由异常。");
        CultistCircle.CultistCircleRecipeService.Verify();
        SeasonDocuments.SeasonDocumentLocationService.Verify();
        StoryGuide.StoryGuideService.Verify();
        HideoutProfit.HideoutProfitService.Verify();
    }
}
