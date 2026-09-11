using System.IO;
using System.Windows;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.MobileMap;

public sealed class MobileMapFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.MobileMap,
        "手机地图",
        "手机",
        95);

    public FrameworkElement CreateView(IFeatureHost host, string route) => new MobileMapView(host);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.MobileMap, StringComparison.Ordinal))
            throw new InvalidOperationException("手机地图组件路由异常。");
        foreach (var file in new[] { "index.html", "app.css", "app.js" })
            if (!File.Exists(Path.Combine(MobileMapRuntime.WebDirectory, file)))
                throw new InvalidOperationException($"手机地图网页资源缺失：{file}");
    }
}
