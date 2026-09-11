using System.Windows;
using System.IO;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.InGamePrice.Models;
using TarkovMapLocator.Modules.InGamePrice.Services;

namespace TarkovMapLocator.Modules.InGamePrice;

public sealed class InGamePriceFeatureModule : IFeatureModule, IFeatureModuleSelfTest
{
    public FeatureDescriptor Descriptor { get; } = new(
        FeatureRoutes.InGamePrice,
        "战局查价",
        "识价",
        50);

    public FrameworkElement CreateView(IFeatureHost host, string route)
    {
        InGamePriceModuleContext.Initialize(host);
        return new InGamePriceView(host);
    }

    public void Verify()
    {
        if (!string.Equals(Descriptor.Id, FeatureRoutes.InGamePrice, StringComparison.Ordinal))
            throw new InvalidOperationException("战局查价组件路由异常。");
        if (!InGamePriceRecognitionSettings.Default.Normalize().Equals(InGamePriceRecognitionSettings.Default))
            throw new InvalidOperationException("战局查价默认配置异常。");
        if (!InGamePriceWindowLocator.LooksLikeWeightLine("0.010 kg"))
            throw new InvalidOperationException("战局查价窗口识别规则异常。");

        string[] requiredFiles =
        [
            "OpenCvSharp.dll",
            "OpenCvSharpExtern.dll",
            "onnxruntime.dll",
            "Microsoft.ML.OnnxRuntime.dll",
            Path.Combine("assets", "in-game-price", "ppocr", "ch_PP-OCRv4_det_infer.onnx"),
            Path.Combine("assets", "in-game-price", "ppocr", "ch_PP-OCRv4_rec_infer.onnx"),
            Path.Combine("assets", "in-game-price", "ppocr", "ppocr_keys_v1.txt")
        ];
        var missing = requiredFiles
            .Where(file => !File.Exists(Path.Combine(InGamePriceModuleContext.ModuleDirectory, file)))
            .ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException($"战局查价组件依赖不完整：{string.Join("、", missing)}");

        try
        {
            if (string.IsNullOrWhiteSpace(OpenCvSharp.Cv2.GetVersionString()))
                throw new InvalidOperationException("OpenCV 未返回版本信息。");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("战局查价 OpenCV 原生运行库不可用。", exception);
        }

        var item = new FeatureMarketItem(
            "recognizer-test", "测试物品", "测试", "Test item", "Test",
            1000, 800, 1200, 700, 700, null, [], "", "", [], null, null, 2, 1);
        var match = new InGamePriceLookupIndex([item]).FindBest("测试物品", .7);
        if (match?.Item.Id != item.Id || InGamePriceOverlayFormatter.GetEffectivePerSlotPrice(item) != 350)
            throw new InvalidOperationException("战局查价索引或单格价格计算异常。");
    }
}
