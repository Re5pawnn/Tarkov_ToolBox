using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

internal static class ApplicationSelfTest
{
    public static async Task<int> RunAsync()
    {
        try
        {
            VerifyRuntimeFiles();
            VerifyMapCatalog();
            VerifyCustomsSatelliteMap();
            VerifyMapLayerAssets();
            VerifyProjection();
            VerifyExtractionRequirements();
            VerifyLargeTrackerBudget();
            VerifyBtrComponent();
            VerifyInstalledFeatureModules();
            if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "bootstrap-data")))
                PackagedBootstrapDataService.ValidatePackageSeed();
            VerifyAvailableCaches();

            // Constructing the window validates compiled XAML, resource keys and
            // every eagerly-created service without presenting UI to the user.
            var window = new MainWindow();
            try { window.VerifyInstalledModuleViews(); }
            finally
            {
                await window.PrepareForCloseAsync();
                window.Close();
            }
            RuntimeLogService.Info("自检", "发布后自检通过", "启动依赖、地图投影、超大预算、BTR 组件、可用缓存和主窗口资源均正常。");
            return 0;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Error("自检", "发布后自检失败", exception);
            return 2;
        }
    }

    private static void VerifyMapCatalog()
    {
        var catalog = MapCatalogService.Load();
        string[] expectedMaps =
        [
            "customs", "streets-of-tarkov", "interchange", "lighthouse", "shoreline", "reserve",
            "woods", "ground-zero", "factory", "the-lab", "the-labyrinth", "icebreaker", "terminal"
        ];
        if (catalog.SchemaVersion != 1 || catalog.DataVersion != "2026.08.14" || catalog.Maps.Count != expectedMaps.Length)
            throw new InvalidDataException("地图资料版本或条目数量异常。");
        var missing = expectedMaps.Where(id => catalog.Maps.All(map => !string.Equals(map.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"地图资料缺少：{string.Join("、", missing)}");
    }

    private static void VerifyRuntimeFiles()
    {
        string[] requiredFiles =
        [
            "TarkovMapLocator.dll",
            "TarkovMapLocator.Core.dll",
            "TarkovMapLocator.pri",
            "Microsoft.WindowsAppRuntime.dll",
            "WinRT.Runtime.dll"
        ];
        var missing = requiredFiles.Where(file => !File.Exists(Path.Combine(AppContext.BaseDirectory, file))).ToArray();
        if (missing.Length > 0) throw new FileNotFoundException($"缺少运行依赖：{string.Join("、", missing)}");

        var environment = StartupEnvironmentCheckService.CheckAppLocalVcRuntime();
        if (!environment.IsReady)
            throw new DllNotFoundException($"VC++ 原生运行库未能随程序部署：{string.Join("；", environment.Problems)}");
    }

    private static void VerifyCustomsSatelliteMap()
    {
        string[] satelliteMapIds =
        [
            "customs", "shoreline", "reserve", "woods", "ground-zero", "factory", "the-labyrinth", "icebreaker"
        ];
        var satelliteLayerCount = 0;
        foreach (var mapId in satelliteMapIds)
        {
            if (!MapBaseProjectionService.TryGetWebMap(mapId, out var definition))
                throw new InvalidDataException($"卫星地图配置缺少：{mapId}");
            var imagePath = Path.Combine(AppContext.BaseDirectory, definition.Image.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(imagePath)) throw new FileNotFoundException($"卫星地图资源未进入发布包：{mapId}", imagePath);
            var size = ReadImageSize(imagePath);
            if (size.Width != definition.PixelSize[0] || size.Height != definition.PixelSize[1])
                throw new InvalidDataException($"卫星地图尺寸与投影清单不一致：{mapId}");
            foreach (var layer in definition.Layers ?? [])
            {
                satelliteLayerCount++;
                var layerPath = Path.Combine(AppContext.BaseDirectory, layer.Image.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(layerPath)) throw new FileNotFoundException($"卫星地图楼层未进入发布包：{mapId}/{layer.Id}", layerPath);
                var layerSize = ReadImageSize(layerPath);
                if (layerSize.Width != definition.PixelSize[0] || layerSize.Height != definition.PixelSize[1])
                    throw new InvalidDataException($"卫星地图楼层尺寸不一致：{mapId}/{layer.Id}");
                if (layer.Extents is not { Length: > 0 })
                    throw new InvalidDataException($"卫星地图楼层缺少高度范围：{mapId}/{layer.Id}");
            }
        }
        if (satelliteLayerCount != 30)
            throw new InvalidDataException($"卫星地图楼层数量异常：{satelliteLayerCount}/30");
        string[] excludedMapIds = ["the-lab", "streets-of-tarkov", "interchange", "lighthouse", "terminal"];
        if (excludedMapIds.Any(MapBaseProjectionService.HasWebMap))
            throw new InvalidDataException("卫星地图清单包含不应覆盖的地图；实验室必须继续沿用原有底图。");

        if (!MapBaseProjectionService.TryGetWebMap("customs", out var customsWebMap))
            throw new InvalidDataException("海关卫星地图配置无法读取。");
        var projected = MapBaseProjectionService.ProjectWebWorld(customsWebMap, -109.2, 22.3);
        var expectedX = (16 * (168.65 - .239 * -109.2) - 29) / 4092;
        var expectedY = (16 * (136.35 + .239 * 22.3) - 1007) / 2081;
        if (Math.Abs(projected.X - expectedX) > .000001 || Math.Abs(projected.Y - expectedY) > .000001)
            throw new InvalidDataException("海关卫星地图未保持 Tarkov Helper 坐标逻辑。");

        var rawCoordinateMarker = new MapMarker
        {
            Type = "self-test",
            Label = "raw-coordinate",
            X = .1,
            Y = .1,
            WorldX = -109.2,
            WorldZ = 22.3
        };
        var rawProjected = MapBaseProjectionService.ProjectMarker(
            rawCoordinateMarker,
            "customs",
            "satellite-map",
            MapBaseProjectionService.Resolve("customs", "satellite-map"));
        if (Math.Abs(rawProjected.X - expectedX) > .000001 || Math.Abs(rawProjected.Y - expectedY) > .000001)
            throw new InvalidDataException("卫星地图没有优先使用原始游戏 X/Z 坐标。");

        if (!MapBaseProjectionService.TryGetWebMap("factory", out var factoryWebMap))
            throw new InvalidDataException("工厂卫星地图配置无法读取。");
        var factoryProjected = MapBaseProjectionService.ProjectWebWorld(factoryWebMap, 10, 20);
        var factoryExpectedX = (16 * (119.9 - 1.629 * 20) - factoryWebMap.Crop.X) / factoryWebMap.Crop.Width;
        var factoryExpectedY = (16 * (139.3 - 1.629 * 10) - factoryWebMap.Crop.Y) / factoryWebMap.Crop.Height;
        if (Math.Abs(factoryProjected.X - factoryExpectedX) > .000001 ||
            Math.Abs(factoryProjected.Y - factoryExpectedY) > .000001)
            throw new InvalidDataException("工厂卫星地图的 90 度坐标旋转异常。");
    }

    private static void VerifyProjection()
    {
        var bounds = new MapCoordinateBounds(100, -100, -100, 100, false, 180);
        if (!bounds.TryProject(0, 0, out var x, out var y) || Math.Abs(x - .5) > .0001 || Math.Abs(y - .5) > .0001)
            throw new InvalidOperationException("地图坐标投影自检失败。");
        if (!bounds.Contains(0, 0) || bounds.Contains(117, 0))
            throw new InvalidOperationException("地图归属判断仍在接受范围外坐标。");

        var factoryBounds = LocalMapPointService.Load().BoundsByMap["factory"];
        if (factoryBounds.PositionRotation != 90 ||
            !factoryBounds.TryProject(58.43222, 63.29811, out var gate3X, out var gate3Y) ||
            Math.Abs(gate3X - .03109848) > .0001 ||
            Math.Abs(gate3Y - .13030021) > .0001)
            throw new InvalidOperationException("Factory coordinate projection is not aligned with the rendered map.");
    }

    private static void VerifyMapLayerAssets()
    {
        var catalog = MapLayerCatalogService.Load();
        if (catalog.ErrorMessage is not null ||
            catalog.LayersByMap.Count != 9 ||
            catalog.AvailableLayerCount != 38 ||
            catalog.MissingAssetCount != 0)
            throw new InvalidDataException($"Layer assets are incomplete: {catalog.ErrorMessage ?? "coverage mismatch"}");

        if (catalog.LayersByMap["reserve"] is not [{ Id: "bunkers" }])
            throw new InvalidDataException("Reserve must expose only the underground bunker layer.");
        if (!catalog.SurfaceNamesByMap.TryGetValue("icebreaker", out var icebreakerSurfaceName) ||
            icebreakerSurfaceName != "医务室")
            throw new InvalidDataException("Icebreaker surface layer name is missing.");

        var interchangeBase = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "maps",
            "native-cache",
            "interchange.png"));
        var interchangeLayers = catalog.LayersByMap["interchange"];
        var interchangeFloorAssets = interchangeLayers
            .Select(layer => File.ReadAllBytes(layer.ImageFilePath))
            .ToArray();
        if (interchangeFloorAssets.Length != 2 ||
            interchangeFloorAssets.Any(bytes => bytes.SequenceEqual(interchangeBase)) ||
            interchangeFloorAssets[0].SequenceEqual(interchangeFloorAssets[1]))
            throw new InvalidDataException("Interchange surface and floor assets are not independent.");

        var labsLayers = catalog.LayersByMap["the-lab"];
        var baseSize = ReadImageSize(Path.Combine(AppContext.BaseDirectory, "assets", "maps", "native-cache", "the-lab.jpg"));
        if (labsLayers.Any(layer => ReadImageSize(layer.ImageFilePath) != baseSize))
            throw new InvalidDataException("Labs surface and floor assets use different pixel bounds.");

        var icebreakerSize = ReadImageSize(Path.Combine(AppContext.BaseDirectory, "assets", "maps", "native-cache", "icebreaker.png"));
        if (catalog.LayersByMap["icebreaker"].Count != 15 ||
            catalog.LayersByMap["icebreaker"].Any(layer => ReadImageSize(layer.ImageFilePath) != icebreakerSize))
            throw new InvalidDataException("Icebreaker floor assets use different pixel bounds.");
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static void VerifyExtractionRequirements()
    {
        var flare = ExtractionRequirementService.BuildToolTip("interchange", "extract", "河畔之路（信号弹）", "pmc");
        var labyrinth = ExtractionRequirementService.BuildToolTip("the-labyrinth", "extract", "阿里阿德涅之线", "pmc");
        var transit = ExtractionRequirementService.BuildToolTip("factory", "transit", "前往实验室", "pmc");
        if (!flare.Contains("绿色信号弹", StringComparison.Ordinal) ||
            !labyrinth.Contains("阿里阿德涅符号钥匙", StringComparison.Ordinal) ||
            !transit.Contains("实验室门禁卡", StringComparison.Ordinal))
            throw new InvalidDataException("撤离条件提示数据不完整。");
        if (ExtractionRequirementService.DataVersion != "2026.07.31" || ExtractionRequirementService.RuleCount < 150)
            throw new InvalidDataException("撤离条件资料版本或覆盖数量异常。");

        string[] supportedMaps =
        [
            "customs", "streets-of-tarkov", "interchange", "lighthouse", "shoreline", "reserve",
            "woods", "ground-zero", "factory", "the-lab", "the-labyrinth", "icebreaker", "terminal"
        ];
        var localPoints = LocalMapPointService.Load();
        if (!ReferenceEquals(localPoints, LocalMapPointService.Load()))
            throw new InvalidDataException("地图点位资料未复用内存缓存。");
        var seasonDocuments = localPoints.MarkersByMap.Values
            .SelectMany(markers => markers)
            .Where(marker => marker.Type == "season-document")
            .ToArray();
        if (seasonDocuments.Length != 350 ||
            seasonDocuments.Any(marker =>
                marker.PreviewImageId?.Length != 32 ||
                !Uri.TryCreate(marker.PreviewImageUrl, UriKind.Absolute, out var imageUri) ||
                imageUri.Scheme != Uri.UriSchemeHttps ||
                !imageUri.Host.Equals("cdn.kaedeori.com", StringComparison.OrdinalIgnoreCase)) ||
            !localPoints.MarkersByMap["customs"].Any(marker =>
                marker.Type == "season-document" && marker.Label == "项目文件" &&
                Math.Abs(marker.WorldX.GetValueOrDefault() - 26.08) < .001 &&
                Math.Abs(marker.WorldHeight.GetValueOrDefault() - 5.83) < .001 &&
                Math.Abs(marker.WorldZ.GetValueOrDefault() + 57.84) < .001))
            throw new InvalidDataException($"赛季文件地图点位或位置截图不完整：{seasonDocuments.Length}/350。");
        var missingMaps = supportedMaps.Where(map => !localPoints.MarkersByMap.ContainsKey(map)).ToArray();
        if (missingMaps.Length > 0)
            throw new InvalidDataException($"撤离条件自检缺少地图：{string.Join("、", missingMaps)}");

        var markersWithoutTips = supportedMaps
            .SelectMany(map => localPoints.MarkersByMap[map].Select(marker => (Map: map, Marker: marker)))
            .Where(item => item.Marker.Type is "extract" or "transit" && string.IsNullOrWhiteSpace(item.Marker.ToolTipText))
            .Select(item => $"{item.Map}/{item.Marker.Label}")
            .ToArray();
        if (markersWithoutTips.Length > 0)
            throw new InvalidDataException($"以下撤离点缺少条件提示：{string.Join("、", markersWithoutTips)}");

        var markersWithoutRules = supportedMaps
            .SelectMany(map => localPoints.MarkersByMap[map].Select(marker => (Map: map, Marker: marker)))
            .Where(item => item.Marker.Type == "extract" &&
                           !ExtractionRequirementService.HasExplicitRule(item.Map, item.Marker.Label, item.Marker.Faction))
            .Select(item => $"{item.Map}/{item.Marker.Label}/{item.Marker.Faction ?? "*"}")
            .ToArray();
        if (markersWithoutRules.Length > 0)
            throw new InvalidDataException($"以下撤离点未建立明确条件规则：{string.Join("、", markersWithoutRules)}");
    }

    private static void VerifyLargeTrackerBudget()
    {
        var item = new TrackerItem("test", "测试", "测试", "", 100_000, 0, 0, 0, 0, 100_000, 0, 0, 100_000, null, [], false, []);
        if (item.EstimatedCost != 10_000_000_000L)
            throw new OverflowException("任务清单的大额预算仍存在 32 位整数溢出风险。");
    }

    private static void VerifyBtrComponent()
    {
        var adapter = new BtrPredictionAdapter();
        if (!adapter.IsAvailable)
            throw new InvalidOperationException($"BTR 预测组件不可用：{adapter.UnavailableReason ?? "未知原因"}");
    }

    private static void VerifyInstalledFeatureModules()
    {
        var catalog = FeatureModuleCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "Modules"));
        if (catalog.Issues.Count > 0)
            throw new InvalidDataException($"可选组件加载失败：{string.Join("；", catalog.Issues.Select(issue => issue.Message))}");
        foreach (var module in catalog.Modules)
            if (module.Instance is TarkovMapLocator.ModuleContracts.IFeatureModuleSelfTest selfTest)
                selfTest.Verify();
    }

    private static void VerifyAvailableCaches()
    {
        var market = MarketPriceService.Load();
        if (market.IsAvailable && !market.IsComplete)
            RuntimeLogService.Warning("自检", "行情缓存缺少部分价格模式", "程序会继续展示可用缓存，并在首次打开相关功能时刷新完整的 PVP/PVE/赛季服数据。");

        var trackerStatus = TaskItemTrackerService.GetCacheStatus();
        if (!trackerStatus.IsAvailable || trackerStatus.IsStale) return;
        var tracker = TaskItemTrackerService.Load("pvp", "", includeTasks: true, includeHideout: true, showCompleted: true);
        if (!tracker.IsAvailable || tracker.TotalEstimatedCost < 0)
            throw new InvalidDataException("任务物品缓存读取或预算汇总失败。");
        var hideoutStations = TaskItemTrackerService.GetHideoutStations("pvp");
        if (hideoutStations.Count == 0 || hideoutStations.Any(station => station.MaxLevel <= 0 || station.CurrentLevel > station.MaxLevel))
            throw new InvalidDataException("藏身处等级数据读取失败。");
    }
}
