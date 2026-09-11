using System.Windows;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Memo;

public sealed class MemoFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.Memo,
        "备忘录",
        "备忘",
        55);

    public FrameworkElement CreateView(IFeatureHost host, string route) => new MemoView(host);

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.Memo, StringComparison.Ordinal))
            throw new InvalidOperationException("备忘录组件路由异常。");

        var normalized = new MemoState(
            1,
            new string('x', 4_100),
            [new MemoItem("item", "测试物品", "测试", "", 20_000), new MemoItem("item", "重复", "", "", 1)],
            true,
            double.NaN,
            120).Normalize();
        if (normalized.Text.Length != 4_000 || normalized.Items.Count != 1 ||
            normalized.Items[0].Quantity != 9_999 || normalized.Left is not null)
            throw new InvalidOperationException("备忘录状态校验异常。");
    }
}
