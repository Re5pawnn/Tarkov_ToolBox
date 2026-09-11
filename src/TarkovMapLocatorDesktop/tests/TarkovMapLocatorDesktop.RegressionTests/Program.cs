using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows.Media.Imaging;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.InGamePrice;
using TarkovMapLocator.Modules.InGamePrice.Models;
using InGamePriceLookupIndex = TarkovMapLocator.Modules.InGamePrice.Services.InGamePriceLookupIndex;
using InGamePriceMatchGate = TarkovMapLocator.Modules.InGamePrice.Services.InGamePriceMatchGate;
using InGamePriceOverlayFormatter = TarkovMapLocator.Modules.InGamePrice.Services.InGamePriceOverlayFormatter;
using EftCaptureTargetService = TarkovMapLocator.Modules.InGamePrice.Services.EftCaptureTargetService;
using TarkovMapLocatorDesktop.Controls;
using TarkovMapLocatorDesktop.Models;
using TarkovMapLocatorDesktop.Services;
using TarkovMapLocatorDesktop.Utilities;
using TarkovMapLocatorDesktop.ViewModels;
using TarkovAutoShade;
using TarkovMapLocator.Modules.ScreenFilter.Models;
using TarkovMapLocator.Modules.MobileMap.Services;
using TarkovMapLocator.Modules.MobileMap;
using TarkovMapLocator.Modules.TeamSync.Models;
using TarkovMapLocator.Modules.TeamSync.Services;
using ScreenGammaService = TarkovMapLocator.Modules.ScreenFilter.Services.ScreenGammaService;
using ScreenFilterPreferencesService = TarkovMapLocator.Modules.ScreenFilter.Services.ScreenFilterPreferencesService;
using ScreenFilterPresetService = TarkovMapLocator.Modules.ScreenFilter.Services.ScreenFilterPresetService;
using GlobalHotkeyService = TarkovMapLocator.Modules.ScreenFilter.Services.GlobalHotkeyService;
using KeyboardInputService = TarkovMapLocator.Modules.ScreenFilter.Services.KeyboardInputService;
using ModuleTaskGraphLayoutService = TarkovMapLocator.Modules.TaskTracking.Services.TaskGraphLayoutService;
using ModuleTaskGraphLineSegment = TarkovMapLocator.Modules.TaskTracking.Services.TaskGraphLineSegment;
using ModuleTaskPrerequisiteCatalogService = TarkovMapLocator.Modules.TaskTracking.Services.TaskPrerequisiteCatalogService;
using ModuleTaskTrackingCatalogService = TarkovMapLocator.Modules.TaskTracking.Services.TaskTrackingCatalogService;
using ModuleTaskTrackingDetailView = TarkovMapLocator.Modules.TaskTracking.Controls.TaskTrackingDetailView;
using ModuleTaskTrackingProgressService = TarkovMapLocator.Modules.TaskTracking.Services.TaskTrackingProgressService;
using ModuleTaskTrackingStatus = TarkovMapLocator.Modules.TaskTracking.Models.TaskTrackingStatus;

namespace TarkovMapLocatorDesktop.RegressionTests;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Any(argument => string.Equals(argument, "--mobile-map-preview", StringComparison.OrdinalIgnoreCase)))
                return RunMobileMapPreviewAsync(args).GetAwaiter().GetResult();
            VerifyApplicationIdentity();
            VerifyFeatureModuleCatalog();
            VerifyMobileMapServerAsync().GetAwaiter().GetResult();
            VerifyMobileMapConnectionLimitAsync().GetAwaiter().GetResult();
            VerifyTeamSyncAuthenticationAsync().GetAwaiter().GetResult();
            VerifyRuntimeLogFiltering();
            VerifyGammaPanelRamp();
            VerifyAutoShadeCore();
            VerifyScreenFilterPersistenceAndRecovery();
            VerifyMapCatalog();
            VerifyCustomsSatelliteMap();
            VerifyMapLayerCatalog();
            VerifyMapLayerMarkerVisibility();
            VerifyTaskTrackingCatalog();
            VerifyTaskProgressBackupRecovery();
            VerifyTaskStatusLogRecognition();
            VerifyExtractionData();
            VerifyMapPointsAndProjection();
            VerifyCoordinateStaleness();
            VerifyAirdropTriangulation();
            VerifyIncrementalCollectionNotifications();
            VerifyIncrementalLogReader();
            VerifyMapTokenRecognition();
            VerifyMapMonitorEvidenceSafety();
            VerifyRaidEndAndUnsupportedMapInvalidation();
            VerifyOrdinaryRouteRecordDoesNotBlockCoordinates();
            VerifySourceOnlyRouteCannotOverrideConfirmedScene();
            VerifyActiveRaidSourceOnlyRouteWaitsForTarget();
            VerifyScreenshotMonitorDetectsNewCoordinate();
            VerifyScreenshotMonitorForcedRecovery();
            VerifyTransferCoordinateIsolation();
            VerifyMapImageDoesNotUpscale();
            VerifyMarketCatalogCompleteness();
            VerifyJsonApiMarketParser();
            VerifyInGamePriceRecognitionSafety();
            VerifyEftCaptureTargetSelection();
            VerifyJsonApiTaskTrackerParser();
            VerifyLargeTrackerBudget();
            VerifyBtrComponent();
            if (args.Any(argument => string.Equals(argument, "--live-icon-check", StringComparison.OrdinalIgnoreCase)))
                VerifyLiveItemIconLoading().GetAwaiter().GetResult();
            if (args.Any(argument => string.Equals(argument, "--live-json-tracker-check", StringComparison.OrdinalIgnoreCase)))
                VerifyLiveJsonTaskTrackerMigration().GetAwaiter().GetResult();
            VerifyModuleShutdownAndStoryModes();
            Console.WriteLine("Regression checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyApplicationIdentity()
    {
        Assert(
            string.Equals(Path.GetFileName(TarkovMapLocatorDesktop.Services.ApplicationIdentity.ApplicationDataDirectory), "TarkovMapLocatorDesktop", StringComparison.Ordinal),
            "The promoted main application must use the stable desktop profile.");
    }

    private static void VerifyFeatureModuleCatalog()
    {
        var utilitiesManifest = FindUpward(Path.Combine(
            "src",
            "TarkovMapLocatorDesktop",
            "bin",
            "Release",
            "net10.0-windows",
            "Modules",
            "Utilities",
            "module.json"));
        var modulesDirectory = Directory.GetParent(Path.GetDirectoryName(utilitiesManifest)!)!.FullName;
        var catalog = FeatureModuleCatalog.Discover(modulesDirectory);
        Assert(catalog.Issues.Count == 0,
            "A valid optional feature module produced a load issue.");
        var expectedModules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FeatureRoutes.Market] = "市场",
            [FeatureRoutes.TaskItems] = "任务物品清单",
            [FeatureRoutes.Memo] = "备忘录",
            [FeatureRoutes.TaskTracking] = "任务追踪",
            [FeatureRoutes.InGamePrice] = "战局查价",
            [FeatureRoutes.ScreenFilter] = "屏幕调色",
            [FeatureRoutes.TeamSync] = "队友位置共享",
            [FeatureRoutes.MobileMap] = "手机地图",
            [FeatureRoutes.Utilities] = "小工具"
        };
        Assert(FeatureRoutes.All.SequenceEqual(expectedModules.Keys),
            "The central route inventory does not match the expected module inventory.");

        var actualIds = catalog.Modules
            .Select(module => module.Instance.Descriptor.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var expectedIds = expectedModules.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert(actualIds.SequenceEqual(expectedIds),
            $"The build output module inventory is incomplete. Expected [{string.Join(", ", expectedIds)}], actual [{string.Join(", ", actualIds)}].");
        var desktopBuildOutput = Directory.GetParent(modulesDirectory)!.FullName;
        Assert(!File.Exists(Path.Combine(desktopBuildOutput, "task-data", "task-game-id-map.json")) &&
               !File.Exists(Path.Combine(desktopBuildOutput, "task-data", "task-tracking-catalog.json")),
            "Task-tracking data leaked into the desktop core build output.");

        foreach (var expected in expectedModules)
        {
            Assert(catalog.TryGet(expected.Key, out var loaded),
                $"The {expected.Key} feature module was not discovered.");
            if (loaded is null) throw new InvalidOperationException($"The {expected.Key} feature module was not loaded.");
            Assert(loaded.Instance.Descriptor.DisplayName == expected.Value,
                $"The {expected.Key} descriptor was not read from the module assembly.");
            Assert(loaded.Instance is IFeatureModuleSelfTest,
                $"The {expected.Key} module must expose its own isolated self-test.");
            ((IFeatureModuleSelfTest)loaded.Instance).Verify();

            var publishedModuleDirectory = Path.GetDirectoryName(loaded.ManifestPath)!;
            Assert(!Directory.EnumerateFiles(publishedModuleDirectory, "TarkovMapLocator.ModuleContracts.dll", SearchOption.AllDirectories).Any(),
                $"The {expected.Key} module contains a duplicate contract assembly.");
            Assert(!Directory.EnumerateFiles(publishedModuleDirectory, "TarkovMapLocatorDesktop.dll", SearchOption.AllDirectories).Any(),
                $"The {expected.Key} module contains the desktop host assembly.");
        }
        var solutionPath = FindUpward("TarkovMapLocator.sln");
        var sourceRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "src");
        var sourceModuleDirectories = Directory.EnumerateDirectories(sourceRoot, "TarkovMapLocator.Modules.*")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert(sourceModuleDirectories.Length == expectedModules.Count,
            $"The source module inventory changed without updating the module contract: {sourceModuleDirectories.Length}/{expectedModules.Count}.");
        foreach (var moduleDirectory in sourceModuleDirectories)
        {
            var manifestPath = Path.Combine(moduleDirectory, "module.json");
            Assert(File.Exists(manifestPath), $"Source module is missing module.json: {moduleDirectory}");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var id = manifest.RootElement.GetProperty("id").GetString() ?? string.Empty;
            var assembly = manifest.RootElement.GetProperty("assembly").GetString() ?? string.Empty;
            Assert(expectedModules.ContainsKey(id), $"Source module has an unregistered route: {id}");
            Assert(string.Equals(assembly, Path.GetFileName(moduleDirectory) + ".dll", StringComparison.Ordinal),
                $"Source module assembly does not match its project directory: {moduleDirectory}");

            var projectPath = Directory.EnumerateFiles(moduleDirectory, "*.csproj", SearchOption.TopDirectoryOnly).Single();
            var project = XDocument.Load(projectPath);
            var projectReferences = project.Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
                .ToArray();
            Assert(projectReferences.All(reference =>
                    !reference.Contains("TarkovMapLocatorDesktop", StringComparison.OrdinalIgnoreCase)),
                $"Optional module references the desktop host project: {moduleDirectory}");
        }


        var desktopSourceRoot = Path.Combine(sourceRoot, "TarkovMapLocatorDesktop");
        string[] forbiddenCoreDirectories =
        [
            Path.Combine("vendor", "in-game-price"),
            Path.Combine("assets", "in-game-price"),
            Path.Combine("ThirdParty", "TarkovAutoShade"),
            Path.Combine("assets", "weapon-build")
        ];
        Assert(forbiddenCoreDirectories.All(path => !Directory.Exists(Path.Combine(desktopSourceRoot, path))),
            "Optional feature assets leaked back into the desktop core source tree.");
        string[] forbiddenCoreFiles =
        [
            Path.Combine(desktopSourceRoot, "task-data", "task-game-id-map.json"),
            Path.Combine(desktopSourceRoot, "task-data", "task-tracking-catalog.json"),
            Path.Combine(desktopSourceRoot, "tools", "update-task-game-id-map.py"),
            Path.Combine(desktopSourceRoot, "tools", "update-task-tracking-catalog.py")
        ];
        Assert(forbiddenCoreFiles.All(path => !File.Exists(path)),
            "Task-tracking data or maintenance tools leaked back into the desktop core source tree.");

        string[] requiredOwnedDirectories =
        [
            Path.Combine(sourceRoot, "TarkovMapLocator.Modules.InGamePrice", "vendor"),
            Path.Combine(sourceRoot, "TarkovMapLocator.Modules.InGamePrice", "assets", "in-game-price"),
            Path.Combine(sourceRoot, "TarkovMapLocator.Modules.ScreenFilter", "ThirdParty", "TarkovAutoShade"),
            Path.Combine(sourceRoot, "TarkovMapLocator.Modules.TaskTracking", "task-data"),
            Path.Combine(sourceRoot, "TarkovMapLocator.Modules.TaskTracking", "tools")
        ];
        Assert(requiredOwnedDirectories.All(Directory.Exists),
            "An optional feature lost ownership of its runtime assets.");

        var desktopProject = XDocument.Load(Path.Combine(desktopSourceRoot, "TarkovMapLocatorDesktop.csproj"));
        var optionalBuildReferences = desktopProject.Descendants("ProjectReference")
            .Where(element => ((string?)element.Attribute("Include"))?.Contains("TarkovMapLocator.Modules.", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        Assert(optionalBuildReferences.Length == expectedModules.Count &&
               optionalBuildReferences.All(element =>
                   string.Equals((string?)element.Attribute("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase)),
            "Desktop build references optional modules as compile-time assemblies.");

        var root = Path.Combine(modulesDirectory, $".regression-invalid-{Guid.NewGuid():N}");
        var unsafeDirectory = Path.Combine(root, "Unsafe");
        var missingDirectory = Path.Combine(root, "MissingAssembly");
        var malformedDirectory = Path.Combine(root, "Malformed");
        Directory.CreateDirectory(unsafeDirectory);
        Directory.CreateDirectory(missingDirectory);
        Directory.CreateDirectory(malformedDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(unsafeDirectory, "module.json"),
                "{\"schemaVersion\":1,\"id\":\"unsafe\",\"assembly\":\"..\\\\outside.dll\",\"entryType\":\"Unsafe.Entry\"}");
            File.WriteAllText(
                Path.Combine(missingDirectory, "module.json"),
                "{\"schemaVersion\":1,\"id\":\"missing\",\"assembly\":\"missing.dll\",\"entryType\":\"Missing.Entry\"}");
            File.WriteAllText(Path.Combine(malformedDirectory, "module.json"), "{not-json");

            var neighborCatalog = FeatureModuleCatalog.Discover(modulesDirectory);
            var neighborIds = neighborCatalog.Modules
                .Select(module => module.Instance.Descriptor.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            Assert(neighborIds.SequenceEqual(expectedIds),
                "Invalid neighboring modules prevented valid modules from loading.");
            Assert(neighborCatalog.Issues.Count(issue => issue.ManifestPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) == 3,
                "Invalid neighboring module manifests were not isolated individually.");
            Assert(neighborCatalog.Issues.Any(issue => issue.ManifestPath.EndsWith(Path.Combine("Unsafe", "module.json"), StringComparison.OrdinalIgnoreCase)),
                "A module manifest was allowed to escape its owned directory.");
            Assert(neighborCatalog.Issues.Any(issue => issue.ManifestPath.EndsWith(Path.Combine("MissingAssembly", "module.json"), StringComparison.OrdinalIgnoreCase)),
                "A missing module assembly was not isolated as a load issue.");
            Assert(neighborCatalog.Issues.Any(issue => issue.ManifestPath.EndsWith(Path.Combine("Malformed", "module.json"), StringComparison.OrdinalIgnoreCase)),
                "A malformed module manifest was not isolated as a load issue.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyMobileMapServerAsync()
    {
        Assert(!MobileMapView.TryNormalizeSakuraFrpEndpoint("frp.example.com:32100", out _) &&
               !MobileMapView.TryNormalizeSakuraFrpEndpoint("http://map.example.com", out _),
            "The SakuraFrp endpoint validator accepted a public endpoint without HTTPS.");
        Assert(MobileMapView.TryNormalizeSakuraFrpEndpoint("https://map.example.com/", out var httpsEndpoint) &&
               httpsEndpoint == "https://map.example.com",
            "A SakuraFrp HTTPS endpoint was not preserved.");
        Assert(!MobileMapView.TryNormalizeSakuraFrpEndpoint("ftp://map.example.com", out _) &&
               !MobileMapView.TryNormalizeSakuraFrpEndpoint("https://map.example.com/path", out _) &&
               !MobileMapView.TryNormalizeSakuraFrpEndpoint("https://map.example.com/?token=unsafe", out _),
            "The SakuraFrp endpoint validator accepted an unsupported public address.");

        var solutionPath = FindUpward("TarkovMapLocator.sln");
        var sourceRoot = Path.GetDirectoryName(solutionPath)!;
        var webDirectory = Path.Combine(sourceRoot, "src", "TarkovMapLocator.Modules.MobileMap", "web");
        var mapPath = Path.Combine(sourceRoot, "src", "TarkovMapLocatorDesktop", "assets", "maps", "web", "customs.png");
        var compactMapPath = Path.Combine(sourceRoot, "src", "TarkovMapLocatorDesktop", "assets", "maps", "mobile-web", "2048", "customs.webp");
        var sharpMapPath = Path.Combine(sourceRoot, "src", "TarkovMapLocatorDesktop", "assets", "maps", "mobile-web", "3072", "customs.webp");
        Assert(File.Exists(Path.Combine(webDirectory, "index.html")) && File.Exists(mapPath) &&
               File.Exists(compactMapPath) && File.Exists(sharpMapPath),
            "Mobile-map test assets are missing.");

        var port = GetFreeTcpPort();
        await using var server = new MobileMapServer(webDirectory);
        server.Start(port);
        server.UpdateSnapshot(CreateMobileMapPreviewSnapshot(mapPath));
        var root = $"http://127.0.0.1:{port}";
        var token = server.Token;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var page = await client.GetAsync($"{root}/?token={token}");
        var pageHtml = await page.Content.ReadAsStringAsync();
        Assert(page.IsSuccessStatusCode && pageHtml.Contains("手机地图", StringComparison.Ordinal),
            "The mobile-map start page was not served.");
        Assert(pageHtml.Contains("id=\"map-load\"", StringComparison.Ordinal) &&
               pageHtml.Contains("id=\"map-load-bar\"", StringComparison.Ordinal) &&
               pageHtml.Contains("id=\"map-load-percent\"", StringComparison.Ordinal),
            "The mobile map omitted its map-loading progress indicator.");
        Assert(pageHtml.Contains("data-filter=\"extract\"", StringComparison.Ordinal) &&
               pageHtml.Contains("data-filter=\"task\"", StringComparison.Ordinal) &&
               pageHtml.Contains("data-filter=\"player\"", StringComparison.Ordinal) &&
               pageHtml.Contains("data-filter=\"peer\"", StringComparison.Ordinal),
            "The mobile map did not expose the expected extract, task, player and teammate filters.");
        var appScript = await client.GetStringAsync($"{root}/app.js");
        Assert(appScript.Contains("type === \"extract\" || type === \"transit\"", StringComparison.Ordinal) &&
               appScript.Contains("type === \"task\"", StringComparison.Ordinal) &&
               appScript.Contains("type === \"peer\"", StringComparison.Ordinal) &&
               appScript.Contains("if (!group) return;", StringComparison.Ordinal) &&
               appScript.Contains("physicalWidth >= 1500", StringComparison.Ordinal) &&
               appScript.Contains("const assetUrl = (key) => `/asset/", StringComparison.Ordinal) &&
               appScript.Contains("response.body.getReader()", StringComparison.Ordinal) &&
               appScript.Contains("received / total * 100", StringComparison.Ordinal) &&
               appScript.Contains("setMapLoadProgress(percent)", StringComparison.Ordinal) &&
               !appScript.Contains("URLSearchParams", StringComparison.Ordinal) &&
               !appScript.Contains("sessionStorage", StringComparison.Ordinal) &&
               !appScript.Contains("authUrl", StringComparison.Ordinal),
            "The mobile map did not merge transits with extracts or reject unsupported marker types.");
        var appStyles = await client.GetStringAsync($"{root}/app.css");
        Assert(!appStyles.Contains(".marker-label { display: none", StringComparison.Ordinal) &&
               !appStyles.Contains(".map-marker:not(.player)", StringComparison.Ordinal) &&
               appStyles.Contains(".map-load-track", StringComparison.Ordinal),
            "The mobile map still hides extract names by default.");

        var snapshotResponse = await client.GetAsync($"{root}/api/snapshot");
        var snapshotJson = await snapshotResponse.Content.ReadAsStringAsync();
        Assert(snapshotResponse.IsSuccessStatusCode && !snapshotJson.Contains("native-cache", StringComparison.OrdinalIgnoreCase),
            "The mobile-map JSON endpoint failed or leaked a local file path.");
        using var snapshot = JsonDocument.Parse(snapshotJson);
        var wireSnapshot = snapshot.RootElement.GetProperty("snapshot");
        var markerTypes = wireSnapshot.GetProperty("markers").EnumerateArray().Select(marker => marker.GetProperty("type").GetString()).ToArray();
        Assert(markerTypes.Contains("task") && markerTypes.Contains("peer"),
            "The mobile-map snapshot dropped task or teammate markers.");
        var assetKey = wireSnapshot.GetProperty("baseAssetKey").GetString();
        var compactAssetKey = wireSnapshot.GetProperty("compactBaseAssetKey").GetString();
        var sharpAssetKey = wireSnapshot.GetProperty("sharpBaseAssetKey").GetString();
        Assert(!string.IsNullOrWhiteSpace(assetKey), "The mobile-map snapshot omitted its registered map asset.");
        Assert(!string.IsNullOrWhiteSpace(compactAssetKey) && !string.IsNullOrWhiteSpace(sharpAssetKey),
            "The mobile-map snapshot omitted its adaptive map assets.");
        var asset = await client.GetAsync($"{root}/asset/{compactAssetKey}");
        Assert(asset.IsSuccessStatusCode && (await asset.Content.ReadAsByteArrayAsync()).Length == new FileInfo(compactMapPath).Length,
            "The mobile-map image endpoint did not return the registered map asset.");
        Assert(asset.Headers.CacheControl?.MaxAge == TimeSpan.FromDays(365) && asset.Headers.CacheControl.Extensions.Any(value => value.Name == "immutable"),
            "The adaptive map asset did not receive stable browser caching headers.");

        using var deniedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var denied = await deniedClient.GetAsync($"{root}/api/snapshot?token=incorrect");
        Assert(denied.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "The mobile-map endpoint accepted an invalid access token.");
        var deniedAsset = await deniedClient.GetAsync($"{root}/asset/{compactAssetKey}");
        Assert(deniedAsset.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "The mobile-map server exposed a registered asset without an authenticated session.");
        var unknownAsset = await client.GetAsync($"{root}/asset/not-registered");
        Assert(unknownAsset.StatusCode == System.Net.HttpStatusCode.NotFound,
            "The mobile-map server exposed an unregistered asset path.");
    }

    private static async Task<int> RunMobileMapPreviewAsync(string[] args)
    {
        var solutionPath = FindUpward("TarkovMapLocator.sln");
        var sourceRoot = Path.GetDirectoryName(solutionPath)!;
        var webDirectory = Path.Combine(sourceRoot, "src", "TarkovMapLocator.Modules.MobileMap", "web");
        var mapPath = Path.Combine(sourceRoot, "src", "TarkovMapLocatorDesktop", "assets", "maps", "web", "customs.png");
        var seconds = 600;
        var optionIndex = Array.FindIndex(args, argument => string.Equals(argument, "--preview-seconds", StringComparison.OrdinalIgnoreCase));
        if (optionIndex >= 0 && optionIndex + 1 < args.Length && int.TryParse(args[optionIndex + 1], out var parsedSeconds))
            seconds = Math.Clamp(parsedSeconds, 5, 3600);

        await using var server = new MobileMapServer(webDirectory);
        var port = GetFreeTcpPort();
        server.Start(port);
        server.UpdateSnapshot(CreateMobileMapPreviewSnapshot(mapPath));
        Console.WriteLine($"MOBILE_MAP_PREVIEW_URL=http://127.0.0.1:{port}/?token={server.Token}");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    private static FeatureMobileMapSnapshot CreateMobileMapPreviewSnapshot(string mapPath)
    {
        var mapsRoot = Directory.GetParent(Path.GetDirectoryName(mapPath)!)!.FullName;
        return new FeatureMobileMapSnapshot(
            "customs",
            "海关",
            "satellite-map",
            null,
            "地面",
            new FeatureMobileMapAsset("customs-base", mapPath, "image/png"),
            null,
            false,
            true,
            true,
            false,
            DateTimeOffset.Now,
            [
                new("player", "player", "当前位置", .53, .49, 32, null),
                new("extract-1", "extract", "宿舍载具", .54, .82, null, null),
                new("extract-2", "extract", "老路大门", .42, .82, null, null),
                new("transit-1", "transit", "前往工厂", .31, .42, null, null),
                new("task-1", "task", "任务目标", .66, .57, null, null),
                new("peer-1", "peer", "队友", .49, .53, 310, "#63c9c4")
            ])
        {
            CompactBaseMap = new FeatureMobileMapAsset("customs-base-compact", Path.Combine(mapsRoot, "mobile-web", "2048", "customs.webp"), "image/webp"),
            SharpBaseMap = new FeatureMobileMapAsset("customs-base-sharp", Path.Combine(mapsRoot, "mobile-web", "3072", "customs.webp"), "image/webp")
        };
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void VerifyRuntimeLogFiltering()
    {
        var entries = new[]
        {
            new RuntimeLogEntry(DateTimeOffset.Now, RuntimeLogLevel.Info, "地图监听", "已识别海关", "证据: 日志"),
            new RuntimeLogEntry(DateTimeOffset.Now, RuntimeLogLevel.Warning, "识价", "截图区域过小", "宽度 120"),
            new RuntimeLogEntry(DateTimeOffset.Now, RuntimeLogLevel.Error, "识价", "OCR 初始化失败", "模型文件缺失")
        };

        Assert(RuntimeLogService.FilterEntries(entries, RuntimeLogLevel.Warning, null, null).Length == 1,
            "Runtime-log level filtering must select the exact requested level.");
        Assert(RuntimeLogService.FilterEntries(entries, null, "识价", null).Length == 2,
            "Runtime-log category filtering did not isolate one feature.");
        var searched = RuntimeLogService.FilterEntries(entries, null, null, "OCR 模型");
        Assert(searched.Length == 1 && searched[0].Level == RuntimeLogLevel.Error,
            "Runtime-log multi-term search must match message and detail together.");
        var formatted = RuntimeLogService.FormatEntries(searched);
        Assert(formatted.Contains("[ERROR]", StringComparison.Ordinal) && formatted.Contains("模型文件缺失", StringComparison.Ordinal),
            "Runtime-log filtered export lost its level or detail text.");
    }

    private static void VerifyGammaPanelRamp()
    {
        var identity = ScreenGammaService.BuildGammaPanelChannelForTest(ScreenFilterChannel.Default);
        Assert(identity.Length == 256, "Gamma Panel identity ramp has an invalid length.");
        for (var index = 0; index < identity.Length; index++)
            Assert(identity[index] == index * 256, $"Gamma Panel identity ramp diverged at {index}.");

        // Captured from the original Gamma Panel 1.0 process after applying
        // Gamma=140, Brightness=10 and Contrast=120. The low byte is zeroed by
        // the original executable before SetDeviceGammaRamp is called.
        var reference = ScreenGammaService.BuildGammaPanelChannelForTest(new ScreenFilterChannel(140, 10, 120));
        var rgb = new ushort[reference.Length * 3];
        Array.Copy(reference, 0, rgb, 0, reference.Length);
        Array.Copy(reference, 0, rgb, reference.Length, reference.Length);
        Array.Copy(reference, 0, rgb, reference.Length * 2, reference.Length);
        var bytes = new byte[rgb.Length * sizeof(ushort)];
        Buffer.BlockCopy(rgb, 0, bytes, 0, bytes.Length);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert(hash == "6467035028DBD9240064A019EBFC8BB2C27A4BAFB0A35945CF10EE69556C3BDA",
            $"Gamma Panel reference ramp changed: {hash}.");

        var preset = new ScreenFilterPreset(
            new ScreenFilterChannel(140, 10, 120),
            new ScreenFilterChannel(80, -20, 70),
            new ScreenFilterChannel(260, 65, 150));
        var independent = ScreenGammaService.BuildGammaPanelRampForTest(preset);
        Assert(independent.Red.SequenceEqual(ScreenGammaService.BuildGammaPanelChannelForTest(preset.Red)),
            "Gamma Panel red channel is not independent.");
        Assert(independent.Green.SequenceEqual(ScreenGammaService.BuildGammaPanelChannelForTest(preset.Green)),
            "Gamma Panel green channel is not independent.");
        Assert(independent.Blue.SequenceEqual(ScreenGammaService.BuildGammaPanelChannelForTest(preset.Blue)),
            "Gamma Panel blue channel is not independent.");
        Assert(!independent.Red.SequenceEqual(independent.Green) && !independent.Green.SequenceEqual(independent.Blue),
            "Independent RGB controls unexpectedly produced identical ramps.");

        Assert(
            ScreenGammaService.BuildGammaPanelChannelForTest(new ScreenFilterChannel(-1, -200, -1))
                .SequenceEqual(ScreenGammaService.BuildGammaPanelChannelForTest(new ScreenFilterChannel(10, -100, 0))),
            "Gamma Panel lower bounds are not normalized.");
        Assert(
            ScreenGammaService.BuildGammaPanelChannelForTest(new ScreenFilterChannel(999, 999, 999))
                .SequenceEqual(ScreenGammaService.BuildGammaPanelChannelForTest(new ScreenFilterChannel(400, 100, 200))),
            "Gamma Panel upper bounds are not normalized.");
        Assert(ScreenGammaService.InterpolateChannelForTest(1000, 5000, 0.0) == 1000 &&
               ScreenGammaService.InterpolateChannelForTest(1000, 5000, 0.5) == 3000 &&
               ScreenGammaService.InterpolateChannelForTest(1000, 5000, 1.0) == 5000,
            "Gamma-ramp transition interpolation changed unexpectedly.");
    }

    private static void VerifyAutoShadeCore()
    {
        var settings = AppSettings.CreateDefault();
        settings.Normalize();
        Assert(settings.AlgorithmVersion >= 11 && settings.ShadowTarget == 70 && settings.HighlightProtection == 76,
            "AutoShade defaults or settings normalization changed unexpectedly.");
        Assert(!settings.AutoWatch && settings.HotkeyKeyCode == 0 && settings.AnalysisHotkeyKeyCode == 0,
            "AutoShade must start in one-shot capture mode without implicit screenshot hotkeys.");
        settings.SmoothTransition = false;
        settings.Normalize();
        Assert(!settings.SmoothTransition, "AutoShade normalization must preserve the user's smooth-transition choice.");

        var migratedSettings = AppSettings.CreateDefault();
        migratedSettings.AlgorithmVersion = 9;
        migratedSettings.AutoWatch = true;
        migratedSettings.HotkeyKeyCode = 119;
        migratedSettings.HotkeyModifiers = 7;
        migratedSettings.Normalize();
        Assert(migratedSettings.AlgorithmVersion == 11 && !migratedSettings.AutoWatch &&
               migratedSettings.HotkeyKeyCode == 0 && migratedSettings.HotkeyModifiers == 0 &&
               migratedSettings.AnalysisHotkeyKeyCode == 0 && migratedSettings.AnalysisHotkeyModifiers == 0,
            "Legacy continuous AutoShade settings were not migrated to one-shot capture safely.");

        var versionTenSettings = AppSettings.CreateDefault();
        versionTenSettings.AlgorithmVersion = 10;
        versionTenSettings.HotkeyKeyCode = 0x2C;
        versionTenSettings.HotkeyModifiers = (int)GlobalHotkeyModifiers.Control;
        versionTenSettings.AnalysisHotkeyKeyCode = 0x77;
        versionTenSettings.AnalysisHotkeyModifiers = (int)GlobalHotkeyModifiers.Shift;
        versionTenSettings.Normalize();
        Assert(versionTenSettings.AlgorithmVersion == 11 &&
               versionTenSettings.HotkeyKeyCode == 0x2C &&
               versionTenSettings.HotkeyModifiers == (int)GlobalHotkeyModifiers.Control &&
               versionTenSettings.AnalysisHotkeyKeyCode == 0 &&
               versionTenSettings.AnalysisHotkeyModifiers == 0,
            "The analysis-hotkey migration must preserve the configured game screenshot shortcut.");

        var screenshotSequence = KeyboardInputService.BuildSequenceForTest(
            new GlobalHotkeyGesture(0x2C, GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift));
        var expectedSequence = new (int VirtualKey, bool KeyUp)[]
        {
            (0x11, false), (0x10, false), (0x2C, false),
            (0x2C, true), (0x10, true), (0x11, true)
        };
        Assert(screenshotSequence.SequenceEqual(expectedSequence),
            "The simulated game screenshot shortcut has an unsafe key-down/key-up order.");
        Assert(KeyboardInputService.NativeInputSizeForTest() == (IntPtr.Size == 8 ? 40 : 28),
            "The native INPUT structure size is invalid for SendInput.");

        var sideButton = new GlobalHotkeyGesture(0x05, GlobalHotkeyModifiers.Control).Normalize();
        Assert(sideButton.IsConfigured && GlobalHotkeyService.IsMouseButtonForTest(sideButton.VirtualKey) &&
               GlobalHotkeyService.Format(sideButton) == "Ctrl + 鼠标侧键 1",
            "Mouse side button 1 is not recognized as a configurable global hotkey.");
        Assert(GlobalHotkeyService.IsMouseButtonForTest(0x06) &&
               !GlobalHotkeyService.IsMouseButtonForTest(0x04),
            "Mouse side-button classification changed unexpectedly.");
        Assert(KeyboardInputService.NativeInputTypesForTest(sideButton)
                .SequenceEqual(new uint[] { 1, 0, 0, 1 }),
            "The simulated mouse-side-button shortcut does not preserve modifier/input ordering.");

        var dark = new AnalysisResult
        {
            P01 = .005,
            P05 = .012,
            P10 = .025,
            P25 = .055,
            Median = .11,
            P75 = .21,
            P90 = .39,
            P95 = .55,
            P99 = .72,
            DynamicRange = .538,
            EdgeEnergy = .035,
            MeanRed = .12,
            MeanGreen = .13,
            MeanBlue = .12,
            UpperMean = .16,
            LowerMean = .09,
            BrightFraction = .01
        };
        var bright = new AnalysisResult
        {
            P01 = .18,
            P05 = .22,
            P10 = .28,
            P25 = .48,
            Median = .68,
            P75 = .82,
            P90 = .93,
            P95 = .98,
            P99 = .995,
            DynamicRange = .76,
            EdgeEnergy = .04,
            MeanRed = .55,
            MeanGreen = .56,
            MeanBlue = .55,
            UpperMean = .79,
            LowerMean = .49,
            BrightFraction = .34
        };
        var nightVision = new AnalysisResult
        {
            P01 = .01,
            P05 = .025,
            P10 = .04,
            P25 = .09,
            Median = .18,
            P75 = .32,
            P90 = .60,
            P95 = .76,
            P99 = .86,
            DynamicRange = .72,
            EdgeEnergy = .04,
            MeanRed = .10,
            MeanGreen = .30,
            MeanBlue = .09,
            UpperMean = .25,
            LowerMean = .12,
            BrightFraction = .04,
            NightVisionScore = .9
        };

        var darkCurve = ToneCurve.Recommend(dark, settings);
        var brightCurve = ToneCurve.Recommend(bright, settings);
        var nightCurve = ToneCurve.Recommend(nightVision, settings);
        ValidateAutoShadeCurve(darkCurve);
        ValidateAutoShadeCurve(brightCurve);
        ValidateAutoShadeCurve(nightCurve);
        Assert(darkCurve.BrightnessBoost > brightCurve.BrightnessBoost,
            "AutoShade no longer lifts dark scenes more than bright scenes.");
        Assert(brightCurve.BrightnessBoost <= 1 && brightCurve.EquivalentGamma <= 1.12,
            "AutoShade bright-scene protection regressed.");
        Assert(nightCurve.BrightnessBoost <= 1 && nightCurve.EquivalentGamma <= 1.20,
            "AutoShade night-vision protection regressed.");
        Assert(ScreenGammaService.IsValidCurveForTest(darkCurve.Red, darkCurve.Green, darkCurve.Blue),
            "AutoShade generated a curve that the shared Gamma service cannot accept.");

        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-autoshade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var received = new ManualResetEventSlim(false);
            string? emittedPath = null;
            using var watcher = new ScreenshotWatcher { Enabled = true };
            watcher.ScreenshotReady += path =>
            {
                emittedPath = path;
                received.Set();
            };
            watcher.SetFolder(directory);

            var imagePath = Path.Combine(directory, "auto-shade-test.png");
            using (var bitmap = new System.Drawing.Bitmap(1280, 720))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(18, 20, 22));
                graphics.FillRectangle(System.Drawing.Brushes.White, 880, 80, 280, 360);
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }

            Assert(received.Wait(TimeSpan.FromSeconds(5)) && string.Equals(emittedPath, imagePath, StringComparison.OrdinalIgnoreCase),
                "AutoShade screenshot watcher did not emit a completed PNG.");
            var analyzed = ImageAnalyzer.Analyze(imagePath, settings);
            Assert(analyzed.IsUsable && analyzed.Recommendation is not null,
                "AutoShade could not analyze a stable screenshot.");
            ValidateAutoShadeCurve(analyzed.Recommendation!);

            var blackPath = Path.Combine(directory, "auto-shade-black.png");
            using (var bitmap = new System.Drawing.Bitmap(1280, 720))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Black);
                bitmap.Save(blackPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            var blackFrame = ImageAnalyzer.Analyze(blackPath, settings);
            Assert(!blackFrame.IsUsable && blackFrame.Recommendation is null &&
                   blackFrame.SkipReason.Contains("全黑", StringComparison.Ordinal),
                "AutoShade must reject an all-black loading or transition frame.");

            var lowResolutionPath = Path.Combine(directory, "auto-shade-low-resolution.png");
            using (var bitmap = new System.Drawing.Bitmap(320, 180))
            {
                bitmap.Save(lowResolutionPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            var lowResolutionFrame = ImageAnalyzer.Analyze(lowResolutionPath, settings);
            Assert(!lowResolutionFrame.IsUsable && lowResolutionFrame.Recommendation is null &&
                   lowResolutionFrame.SkipReason.Contains("分辨率", StringComparison.Ordinal),
                "AutoShade must reject thumbnails and unrelated low-resolution PNG files.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidateAutoShadeCurve(FilterRecommendation recommendation)
    {
        Assert(recommendation.Red.Length == 256 && recommendation.Green.Length == 256 && recommendation.Blue.Length == 256,
            "AutoShade produced a curve with an invalid channel length.");
        Assert(recommendation.Green[0] == 0 && recommendation.Green[^1] == ushort.MaxValue,
            "AutoShade curve endpoints are invalid.");
        for (var index = 1; index < 256; index++)
        {
            Assert(recommendation.Red[index] >= recommendation.Red[index - 1] &&
                   recommendation.Green[index] >= recommendation.Green[index - 1] &&
                   recommendation.Blue[index] >= recommendation.Blue[index - 1],
                $"AutoShade curve is not monotonic at {index}.");
        }
    }

    private static void VerifyScreenFilterPersistenceAndRecovery()
    {
        var savedFilters = ScreenFilterPresetService.ParseForTest(
            "{\"schemaVersion\":1,\"presets\":[{\"id\":\"night-test\",\"name\":\"夜间测试\",\"linkChannels\":false," +
            "\"red\":{\"gamma\":140,\"brightness\":10,\"contrast\":120}," +
            "\"green\":{\"gamma\":150,\"brightness\":20,\"contrast\":130}," +
            "\"blue\":{\"gamma\":160,\"brightness\":30,\"contrast\":140}}]}");
        Assert(savedFilters.Count == 1 && savedFilters[0].Name == "夜间测试" && !savedFilters[0].LinkChannels,
            "A valid custom screen-filter preset was not parsed.");
        Assert(savedFilters[0].Blue == new ScreenFilterChannel(160, 30, 140),
            "Independent RGB values were not preserved in a saved screen-filter preset.");
        Assert(ScreenFilterPresetService.ParseForTest("{\"schemaVersion\":99,\"presets\":[]}").Count == 0,
            "An unsupported custom screen-filter preset schema must be rejected.");

        var configuredHotkey = ScreenFilterPreferencesService.ParseForTest(
            "{\"schemaVersion\":2,\"toggleHotkey\":{\"virtualKey\":119,\"modifiers\":6}}");
        Assert(
            configuredHotkey.ToggleHotkey == new GlobalHotkeyGesture(0x77, GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift),
            "The configured screen-filter hotkey did not survive preference parsing.");
        Assert(
            GlobalHotkeyService.Format(configuredHotkey.ToggleHotkey) == "Ctrl + Shift + F8",
            "The screen-filter hotkey display text is incorrect.");
        var autoShadeMode = ScreenFilterPreferencesService.ParseForTest(
            "{\"schemaVersion\":2,\"mode\":1}");
        Assert(autoShadeMode.Mode == ScreenFilterMode.AutoShade,
            "The selected AutoShade mode did not survive preference parsing.");

        var invalidHotkey = ScreenFilterPreferencesService.ParseForTest(
            "{\"schemaVersion\":2,\"toggleHotkey\":{\"virtualKey\":17,\"modifiers\":15}}");
        Assert(!invalidHotkey.ToggleHotkey.IsConfigured,
            "A modifier-only screen-filter hotkey must be rejected.");

        Assert(ReferenceEquals(
                ScreenFilterPreferencesService.ParseForTest("{\"schemaVersion\":\"2\"}"),
                ScreenFilterRequest.Default),
            "A string screen-filter schema version must fall back to defaults.");
        Assert(ReferenceEquals(
                ScreenFilterPreferencesService.ParseForTest("{\"schemaVersion\":null}"),
                ScreenFilterRequest.Default),
            "A null screen-filter schema version must fall back to defaults.");
        Assert(ReferenceEquals(
                ScreenFilterPreferencesService.ParseForTest("{\"schemaVersion\":2,\"red\":\"invalid\"}"),
                ScreenFilterRequest.Default),
            "Malformed screen-filter channel data must fall back to defaults.");
        Assert(ReferenceEquals(
                ScreenFilterPreferencesService.ParseForTest("not-json"),
                ScreenFilterRequest.Default),
            "Invalid screen-filter JSON must fall back to defaults.");

        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-gamma-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var recoveryPath = Path.Combine(directory, "gamma-recovery-test.json");
            var channel = Enumerable.Range(0, 256).Select(index => (ushort)(index * 256)).ToArray();
            File.WriteAllText(recoveryPath, JsonSerializer.Serialize(new
            {
                OwnerProcessId = 123,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Displays = new[]
                {
                    new { DeviceName = "DISPLAY-A", Red = channel, Green = channel, Blue = channel },
                    new { DeviceName = "DISPLAY-B", Red = channel, Green = channel, Blue = channel }
                }
            }));

            var firstCalls = new List<string>();
            var firstResult = ScreenGammaService.RestoreRecoveryFileForTest(recoveryPath, deviceName =>
            {
                firstCalls.Add(deviceName);
                return string.Equals(deviceName, "DISPLAY-A", StringComparison.Ordinal);
            });
            Assert(!firstResult, "Partial gamma recovery must report an unresolved display.");
            Assert(firstCalls.SequenceEqual(new[] { "DISPLAY-A", "DISPLAY-B" }),
                "Gamma recovery did not attempt each persisted display once.");

            using (var remainingDocument = JsonDocument.Parse(File.ReadAllText(recoveryPath)))
            {
                var remaining = remainingDocument.RootElement.GetProperty("Displays");
                Assert(remaining.GetArrayLength() == 1 &&
                       remaining[0].GetProperty("DeviceName").GetString() == "DISPLAY-B",
                    "Partial gamma recovery did not persist only the unresolved display.");
            }

            var secondCalls = new List<string>();
            var secondResult = ScreenGammaService.RestoreRecoveryFileForTest(recoveryPath, deviceName =>
            {
                secondCalls.Add(deviceName);
                return true;
            });
            Assert(secondResult, "The remaining gamma display was not recovered on retry.");
            Assert(secondCalls.SequenceEqual(new[] { "DISPLAY-B" }),
                "A successfully restored display was retried on a later recovery pass.");
            Assert(!File.Exists(recoveryPath), "Completed gamma recovery did not remove its recovery file.");

            File.WriteAllText(recoveryPath, "{\"OwnerProcessId\":123,\"Displays\":null}");
            var malformedCalled = false;
            var malformedResult = ScreenGammaService.RestoreRecoveryFileForTest(recoveryPath, _ =>
            {
                malformedCalled = true;
                return true;
            });
            Assert(!malformedResult && !malformedCalled,
                "A null gamma recovery display list must be rejected without invoking native recovery.");
            Assert(!File.Exists(recoveryPath), "Malformed gamma recovery data was not quarantined/removed.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyMapCatalog()
    {
        var catalog = MapCatalogService.Load();
        Assert(catalog.SchemaVersion == 1, "Unexpected map catalog schema.");
        Assert(catalog.DataVersion == "2026.08.14", "Unexpected map catalog data version.");
        Assert(catalog.Maps.Count == 13, "Map catalog must contain all 13 supported maps.");
        Assert(catalog.Maps.Select(map => map.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 13, "Map ids must be unique.");
    }

    private static void VerifyCustomsSatelliteMap()
    {
        string[] expectedSatelliteMaps =
        [
            "customs", "shoreline", "reserve", "woods", "ground-zero", "factory", "the-labyrinth", "icebreaker"
        ];
        var satelliteLayerCount = 0;
        foreach (var mapId in expectedSatelliteMaps)
        {
            Assert(MapBaseProjectionService.TryGetWebMap(mapId, out var definition),
                $"Satellite-map projection manifest is missing {mapId}.");
            var imagePath = FindUpward(definition.Image.Replace('/', Path.DirectorySeparatorChar));
            Assert(ReadImageSize(imagePath) == (definition.PixelSize[0], definition.PixelSize[1]),
                $"Satellite-map image dimensions no longer match the projection manifest for {mapId}.");
            foreach (var layer in definition.Layers ?? [])
            {
                satelliteLayerCount++;
                var layerPath = FindUpward(layer.Image.Replace('/', Path.DirectorySeparatorChar));
                Assert(ReadImageSize(layerPath) == (definition.PixelSize[0], definition.PixelSize[1]),
                    $"Satellite-map layer dimensions no longer match {mapId}/{layer.Id}.");
                Assert(layer.Extents is { Length: > 0 },
                    $"Satellite-map layer height extents are missing for {mapId}/{layer.Id}.");
            }
        }
        Assert(satelliteLayerCount == 30, "Satellite-map manifest must contain all 30 independent layers.");
        string[] excludedMapIds = ["the-lab", "streets-of-tarkov", "interchange", "lighthouse", "terminal"];
        Assert(excludedMapIds.All(mapId => !MapBaseProjectionService.HasWebMap(mapId)),
            "Satellite-map mode must not replace Labs or expose maps without satellite tiles.");

        Assert(MapBaseProjectionService.TryGetWebMap("customs", out var customsWebMap),
            "Customs satellite-map projection could not be loaded.");
        var helperProjected = MapBaseProjectionService.ProjectWebWorld(customsWebMap, -109.2, 22.3);
        var expectedHelperX = (16 * (168.65 - .239 * -109.2) - 29) / 4092;
        var expectedHelperY = (16 * (136.35 + .239 * 22.3) - 1007) / 2081;
        Assert(Math.Abs(helperProjected.X - expectedHelperX) < .000001 &&
               Math.Abs(helperProjected.Y - expectedHelperY) < .000001,
            "Tarkov Helper Customs CRS.Simple projection regressed.");
        var rawCoordinateMarker = new MapMarker
        {
            Type = "test",
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
        Assert(Math.Abs(rawProjected.X - expectedHelperX) < .000001 &&
               Math.Abs(rawProjected.Y - expectedHelperY) < .000001,
            "Tarkov Helper test mode must prefer raw game X/Z coordinates over existing normalized points.");
        Assert(MapBaseProjectionService.TryGetWebMap("factory", out var factoryWebMap),
            "Factory satellite-map projection could not be loaded.");
        var factoryProjected = MapBaseProjectionService.ProjectWebWorld(factoryWebMap, 10, 20);
        var expectedFactoryX = (16 * (119.9 - 1.629 * 20) - factoryWebMap.Crop.X) / factoryWebMap.Crop.Width;
        var expectedFactoryY = (16 * (139.3 - 1.629 * 10) - factoryWebMap.Crop.Y) / factoryWebMap.Crop.Height;
        Assert(Math.Abs(factoryProjected.X - expectedFactoryX) < .000001 &&
               Math.Abs(factoryProjected.Y - expectedFactoryY) < .000001,
            "Factory satellite-map 90-degree coordinate rotation regressed.");

        var preferences = DesktopPreferences.Empty with
        {
            SelectedMapStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["customs"] = "satellite",
                ["woods"] = "unsupported"
            }
        };
        var normalized = preferences.Normalize();
        Assert(normalized.SelectedMapStyles is { Count: 1 } && normalized.SelectedMapStyles["customs"] == "satellite-map",
            "Legacy manual calibration style was not migrated to satellite-map mode.");
        var helperPreferences = (preferences with
        {
            SelectedMapStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["customs"] = "helper-test",
                ["woods"] = "web-map"
            }
        }).Normalize();
        Assert(helperPreferences.SelectedMapStyles is { Count: 2 } &&
               helperPreferences.SelectedMapStyles["customs"] == "satellite-map" &&
               helperPreferences.SelectedMapStyles["woods"] == "satellite-map",
            "Map-style preference normalization did not migrate legacy web-map mode to satellite-map.");

        var mapWorkspacePreferences = (DesktopPreferences.Empty with
        {
            MapInspectorPaneWidth = 480,
            MapInspectorPaneHeight = 680
        }).Normalize();
        Assert(mapWorkspacePreferences.MapInspectorPaneWidth == 480 &&
               mapWorkspacePreferences.MapInspectorPaneHeight == 680,
            "Map/task splitter dimensions were not preserved by preference normalization.");
        var invalidMapWorkspacePreferences = (DesktopPreferences.Empty with
        {
            MapInspectorPaneWidth = 120,
            MapInspectorPaneHeight = 180
        }).Normalize();
        Assert(invalidMapWorkspacePreferences.MapInspectorPaneWidth is null &&
               invalidMapWorkspacePreferences.MapInspectorPaneHeight is null,
            "Invalid map/task splitter dimensions were not discarded.");
    }

    private static void VerifyMapLayerCatalog()
    {
        var catalog = MapLayerCatalogService.Load();
        Assert(catalog.ErrorMessage is null, $"Layer catalog failed to load: {catalog.ErrorMessage}");
        Assert(catalog.LayersByMap.Count == 9, "Layer catalog must cover all 9 layered maps.");
        Assert(catalog.AvailableLayerCount == 38, "Layer asset coverage regressed.");
        Assert(catalog.MissingAssetCount == 0, "A declared layer asset is missing.");
        Assert(
            catalog.LayersByMap["reserve"] is [{ Id: "bunkers" }],
            "Reserve must expose only the underground bunker layer.");
        Assert(
            catalog.SurfaceNamesByMap.TryGetValue("icebreaker", out var icebreakerSurfaceName) && icebreakerSurfaceName == "医务室",
            "Icebreaker surface layer name is missing.");

        var interchangeBase = File.ReadAllBytes(FindUpward(Path.Combine("assets", "maps", "native-cache", "interchange.png")));
        var interchangeLayers = catalog.LayersByMap["interchange"];
        Assert(
            interchangeLayers.Count == 2 &&
            interchangeLayers.Select(layer => File.ReadAllBytes(layer.ImageFilePath))
                .All(bytes => !bytes.SequenceEqual(interchangeBase)) &&
            !File.ReadAllBytes(interchangeLayers[0].ImageFilePath)
                .SequenceEqual(File.ReadAllBytes(interchangeLayers[1].ImageFilePath)),
            "Interchange floor overlays must not duplicate the surface asset.");

        var labsLayers = catalog.LayersByMap["the-lab"];
        var basePath = FindUpward(Path.Combine("assets", "maps", "native-cache", "the-lab.jpg"));
        var baseSize = ReadImageSize(basePath);
        Assert(
            labsLayers.All(layer => ReadImageSize(layer.ImageFilePath) == baseSize),
            "Labs surface and floor assets must use the same crop and pixel dimensions.");

        var icebreakerLayers = catalog.LayersByMap["icebreaker"];
        var icebreakerSize = ReadImageSize(FindUpward(Path.Combine("assets", "maps", "native-cache", "icebreaker.png")));
        Assert(
            icebreakerLayers.Count == 15 && icebreakerLayers.All(layer => ReadImageSize(layer.ImageFilePath) == icebreakerSize),
            "Icebreaker must expose all 16 aligned floors, including its surface layer.");
    }

    private static void VerifyMapLayerMarkerVisibility()
    {
        var underground = new MapLayerDefinition
        {
            MapId = "test",
            Id = "underground",
            Name = "地下层",
            ImageFileName = "test.png",
            ImageFilePath = "test.png",
            Extents =
            [
                new MapLayerExtent
                {
                    MinimumHeight = -20,
                    MaximumHeight = -2,
                    Bounds = [new MapLayerWorldBounds(-10, -10, 10, 10)]
                }
            ]
        };
        MapLayerDefinition[] layers = [underground];
        var basement = new MapMarker
        {
            Type = "task",
            Label = "地下任务",
            X = .5,
            Y = .5,
            WorldX = 0,
            WorldHeight = -5,
            WorldZ = 0
        };
        var surface = new MapMarker
        {
            Type = "extract",
            Label = "手工表层出口",
            X = .5,
            Y = .5
        };

        Assert(!MapLayerVisibilityService.IsVisible(basement, null, layers), "A floor marker leaked onto the surface map.");
        Assert(MapLayerVisibilityService.IsVisible(basement, underground, layers), "A floor marker disappeared from its matching layer.");
        Assert(MapLayerVisibilityService.IsVisible(surface, null, layers), "A surface-only marker disappeared from the surface map.");
        Assert(!MapLayerVisibilityService.IsVisible(surface, underground, layers), "A surface-only marker leaked onto a floor overlay.");
    }

    private static void VerifyTaskTrackingCatalog()
    {
        var loaded = ModuleTaskTrackingCatalogService.Load();
        Assert(loaded.Catalog is not null && loaded.ErrorMessage is null,
            $"Task tracking catalog failed to load: {loaded.ErrorMessage}");
        Assert(loaded.Catalog!.Tasks.Count == 1560, "The combined Kaedeori catalog no longer contains all three 520-task modes.");
        var modeCounts = loaded.Catalog.Tasks
            .GroupBy(task => task.Mode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        Assert(modeCounts.Count == 3 &&
               modeCounts.GetValueOrDefault("pvp") == 520 &&
               modeCounts.GetValueOrDefault("pve") == 520 &&
               modeCounts.GetValueOrDefault("season") == 520,
            "PVP, PVE and seasonal task catalogs are not independently complete.");
        Assert(loaded.Catalog.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() == 1560,
            "Task tracking ids collide across game modes.");
        Assert(loaded.Catalog.Tasks.Where(task => task.Mode == "pvp").All(task => task.Id.StartsWith("pvp:", StringComparison.Ordinal)) &&
               loaded.Catalog.Tasks.Where(task => task.Mode == "season").All(task => task.Id.StartsWith("season:", StringComparison.Ordinal)) &&
               loaded.Catalog.Tasks.Where(task => task.Mode == "pve").All(task => !task.Id.StartsWith("pvp:", StringComparison.Ordinal) && !task.Id.StartsWith("season:", StringComparison.Ordinal)),
            "Task progress ids are not isolated by game mode.");
        var migratedProgress = ModuleTaskTrackingProgressService.PartitionLegacyStatuses(
            new Dictionary<string, ModuleTaskTrackingStatus>(StringComparer.Ordinal)
            {
                ["legacy-pve-id"] = ModuleTaskTrackingStatus.Completed,
                ["pvp:task-id"] = ModuleTaskTrackingStatus.Accepted,
                ["season:task-id"] = ModuleTaskTrackingStatus.Completed
            });
        Assert(migratedProgress["pve"].ContainsKey("legacy-pve-id") &&
               migratedProgress["pvp"].ContainsKey("pvp:task-id") &&
               migratedProgress["season"].ContainsKey("season:task-id") &&
               migratedProgress.Values.Sum(statuses => statuses.Count) == 3,
            "Legacy task progress is not partitioned cleanly into PVP, PVE and seasonal storage.");
        Assert(loaded.Catalog.SourceUrl.Contains("member.kaedeori.com/app/taskslist", StringComparison.Ordinal),
            "Task tracking no longer identifies the Kaedeori current-task page as its source.");
        Assert(loaded.Catalog.Tasks.All(task => task.GroupKey is "loyalty1" or "loyalty2" or "loyalty3" or "loyalty4" or "core"),
            "A current task escaped the loyalty/core grouping model.");
        Assert(loaded.Catalog.Tasks.All(task => !string.Equals(task.MapName, "Any", StringComparison.OrdinalIgnoreCase)),
            "Raw Any map labels leaked into the Chinese task interface.");
        Assert(ModuleTaskTrackingDetailView.CleanMarkdown("==🚗任务目标==") == "任务目标" &&
               ModuleTaskTrackingDetailView.CleanMarkdown("==😮☝️任务攻略==") == "任务攻略",
            "Task guide headings no longer remove source emoji decoration.");

        var therapistTasks = loaded.Catalog.Tasks
            .Where(task => task.Mode == "pve" && string.Equals(task.TraderKey, "therapist", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var therapistCounts = therapistTasks
            .GroupBy(task => task.GroupKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert(therapistCounts.GetValueOrDefault("loyalty1") == 9 &&
               therapistCounts.GetValueOrDefault("loyalty2") == 9 &&
               therapistCounts.GetValueOrDefault("loyalty3") == 8 &&
               therapistCounts.GetValueOrDefault("loyalty4") == 3 &&
               therapistCounts.GetValueOrDefault("core") == 22,
            "Therapist's Kaedeori loyalty/core grouping changed unexpectedly.");
        Assert(therapistTasks.Any(task => task.Name == "带血的水" && task.GroupKey == "loyalty1"),
            "Kaedeori task grouping lost Bloodied Water from Therapist loyalty I.");
        Assert(therapistTasks.Any(task => task.Name == "秋季综合征" && task.GroupKey == "loyalty1"),
            "Kaedeori task grouping lost Fall Ailment from Therapist loyalty I.");
        Assert(loaded.Catalog.Tasks.Count(task => task.PreviousTaskIds.Count > 0) >= 330,
            "Explicit Kaedeori prerequisite coverage regressed.");

        var gameIdMapPath = Path.Combine(
            AppContext.BaseDirectory,
            "Modules",
            "TaskTracking",
            "task-data",
            "task-game-id-map.json");
        using var gameIdMap = JsonDocument.Parse(File.ReadAllText(gameIdMapPath));
        var mappedTaskCount = gameIdMap.RootElement.GetProperty("mappings").GetArrayLength();
        Assert(mappedTaskCount >= 1290,
            $"Unique Kaedeori task status game-id coverage regressed ({mappedTaskCount} mapped tasks).");
        var prerequisiteSetCount = gameIdMap.RootElement.GetProperty("prerequisites").GetArrayLength();
        Assert(prerequisiteSetCount >= 330,
            $"Explicit Kaedeori prerequisite coverage regressed ({prerequisiteSetCount} tasks with prerequisites).");
        var prerequisites = ModuleTaskPrerequisiteCatalogService.Load();
        var retry = loaded.Catalog.Tasks.Single(task => task.Mode == "pve" && task.Name == "风火轮 - 再次尝试");
        var hotWheels = loaded.Catalog.Tasks.Single(task => task.Mode == "pve" && task.Name == "风火轮");
        Assert(prerequisites.TryGetValue(retry.Id, out var retryPrerequisites) &&
               retryPrerequisites.Contains(hotWheels.Id, StringComparer.Ordinal),
            "A known Kaedeori direct prerequisite was lost.");

        var praporTasks = loaded.Catalog.Tasks
            .Where(task => task.Mode == "pve" && string.Equals(
                string.IsNullOrWhiteSpace(task.SectionKey) ? task.TraderKey : task.SectionKey,
                "prapor",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var compactLayout = ModuleTaskGraphLayoutService.Build(praporTasks, loaded.Catalog.Lines);
        Assert(compactLayout.Positions.Count == praporTasks.Length,
            "The compact task layout dropped Prapor tasks.");
        Assert(compactLayout.Groups.Count == 5,
            "Prapor tasks are no longer separated into loyalty I-IV and core groups.");
        Assert(compactLayout.Width is > 0 and < 1800 && compactLayout.Height is > 0 and < 3000,
            $"The per-trader task layout is no longer compact ({compactLayout.Width:0} x {compactLayout.Height:0}).");
        Assert(compactLayout.Segments.Count >= 13,
            "The explicit Prapor prerequisite connector lines were dropped.");
        var compactPositions = compactLayout.Positions.Values.ToArray();
        Assert(!compactPositions.Where((first, firstIndex) =>
                compactPositions.Skip(firstIndex + 1).Any(first.IntersectsWith)).Any(),
            "Compact task cards overlap each other.");
        Assert(compactLayout.Segments.All(segment => segment.IsSourceAligned),
            "The Prapor graph contains a diagonal prerequisite segment.");
        Assert(compactLayout.Segments.All(segment =>
                compactPositions.All(position => !CrossesCardInterior(segment, position))),
            "A prerequisite connector crosses an unrelated task card.");
        var letterTask = praporTasks.Single(task => task.Name == "一信之缘");
        var letterBounds = compactLayout.Positions[letterTask.Id];
        Assert(compactLayout.Segments.All(segment => !CrossesCardInterior(segment, letterBounds)),
            "An unrelated prerequisite connector still appears attached to 一信之缘.");
        foreach (var graph in loaded.Catalog.Tasks.GroupBy(task => new { task.Mode, task.TraderKey }))
        {
            var graphTasks = graph.ToArray();
            var graphLayout = ModuleTaskGraphLayoutService.Build(graphTasks, loaded.Catalog.Lines);
            var graphCards = graphLayout.Positions.Values.ToArray();
            Assert(graphLayout.Segments.All(segment => segment.IsSourceAligned),
                $"The {graph.Key.Mode}/{graph.Key.TraderKey} graph contains a diagonal connector.");
            Assert(graphLayout.Segments.All(segment =>
                    graphCards.All(card => !CrossesCardInterior(segment, card))),
                $"The {graph.Key.Mode}/{graph.Key.TraderKey} graph routes a connector through a task card.");
        }

        static bool CrossesCardInterior(ModuleTaskGraphLineSegment segment, System.Windows.Rect card)
        {
            if (segment.Start.X == segment.End.X)
            {
                var top = Math.Min(segment.Start.Y, segment.End.Y);
                var bottom = Math.Max(segment.Start.Y, segment.End.Y);
                return segment.Start.X > card.Left && segment.Start.X < card.Right &&
                       bottom > card.Top && top < card.Bottom;
            }
            if (segment.Start.Y == segment.End.Y)
            {
                var left = Math.Min(segment.Start.X, segment.End.X);
                var right = Math.Max(segment.Start.X, segment.End.X);
                return segment.Start.Y > card.Top && segment.Start.Y < card.Bottom &&
                       right > card.Left && left < card.Right;
            }
            return true;
        }
    }

    private static void VerifyTaskStatusLogRecognition()
    {
        Assert(TaskStatusLogMonitorService.TryResolveStatus("description", out var accepted) &&
               accepted == TaskTrackingStatus.Accepted,
            "Quest-start messages no longer map to the accepted state.");
        Assert(TaskStatusLogMonitorService.TryResolveStatus("successMessageText", out var completed) &&
               completed == TaskTrackingStatus.Completed,
            "Quest-finish messages no longer map to the completed state.");
        Assert(TaskStatusLogMonitorService.TryResolveStatus("failMessageText", out var failed) &&
               failed == TaskTrackingStatus.NotStarted,
            "Quest-failure messages no longer clear the active task state.");

        var root = Path.Combine(Path.GetTempPath(), $"tml-task-log-{Guid.NewGuid():N}", "Logs");
        var session = Path.Combine(root, "log_2026.08.28_10-00-00_1.1.0.1.46911");
        Directory.CreateDirectory(session);
        var logPath = Path.Combine(session, "2026.08.28_10-00-00_1.1.0.1.46911 push-notifications_000.log");
        const string gameTaskId = "5967733e86f7746d9c1d2d8b";
        var monitor = new TaskStatusLogMonitorService(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [gameTaskId] = ["1", "pvp:1", "season:1"]
        });

        try
        {
            File.WriteAllText(logPath,
                "2026-08-28 10:01:00.000|1|Info|push-notifications|Got notification | ChatMessageReceived\n" +
                "{\n  \"message\": {\n    \"text\": \"quest started\",\n" +
                $"    \"templateId\": \"{gameTaskId} description\"\n  }}\n}}\n" +
                "2026-08-28 10:02:00.000|1|Info|push-notifications|Got notification | ChatMessageReceived\n" +
                "{\n  \"message\": {\n    \"text\": \"quest started\",\n" +
                $"    \"templateId\": \"{gameTaskId} successMessageText\"\n  }}\n}}\n");
            var initial = monitor.ReadLatest(root);
            Assert(initial.Count == 1 && initial[0].TrackingTaskId == "1" && initial[0].Status == TaskTrackingStatus.Completed,
                "Historical task log replay did not keep the latest status for a task.");
            Assert(monitor.ReadLatest(root).Count == 0,
                "An unchanged task log was applied more than once.");

            monitor.SetMode("pvp");
            Assert(monitor.ReadLatest(root, replayHistoryWhenUninitialized: false).Count == 0,
                "Switching to PVP replayed PVE history into the PVP task state.");
            File.AppendAllText(logPath,
                "2026-08-28 10:03:00.000|1|Info|push-notifications|Got notification | ChatMessageReceived\n" +
                "{\n  \"message\": {\n    \"text\": \"quest started\",\n" +
                $"    \"templateId\": \"{gameTaskId} description\"\n  }}\n}}\n");
            var pvpIncremental = monitor.ReadLatest(root, replayHistoryWhenUninitialized: false);
            Assert(pvpIncremental.Count == 1 && pvpIncremental[0].TrackingTaskId == "pvp:1" && pvpIncremental[0].Status == TaskTrackingStatus.Accepted,
                "A new PVP task event was not confined to the active PVP state.");

            monitor.SetMode("season");
            Assert(monitor.ReadLatest(root, replayHistoryWhenUninitialized: false).Count == 0,
                "Switching to seasonal tasks replayed another mode's history.");
            File.AppendAllText(logPath,
                "2026-08-28 10:04:00.000|1|Info|push-notifications|Got notification | ChatMessageReceived\n" +
                "{\n  \"message\": {\n    \"text\": \"quest started\",\n" +
                $"    \"templateId\": \"{gameTaskId} successMessageText\"\n  }}\n}}\n");
            var seasonIncremental = monitor.ReadLatest(root, replayHistoryWhenUninitialized: false);
            Assert(seasonIncremental.Count == 1 && seasonIncremental[0].TrackingTaskId == "season:1" && seasonIncremental[0].Status == TaskTrackingStatus.Completed,
                "A new seasonal task event was not confined to the active seasonal state.");

            monitor.SetMode("pve");
            Assert(monitor.ReadLatest(root, replayHistoryWhenUninitialized: false).Count == 0,
                "Switching back to PVE imported events recorded while another mode was active.");
            var explicitReplay = monitor.ReplayHistory(root);
            Assert(explicitReplay.Count == 1 && explicitReplay[0].TrackingTaskId == "1" && explicitReplay[0].Status == TaskTrackingStatus.Completed,
                "Explicit task synchronization no longer replays history for the selected mode.");

            File.AppendAllText(logPath,
                "2026-08-28 10:05:00.000|1|Info|push-notifications|Got notification | ChatMessageReceived\n" +
                "{\n  \"message\": {\n    \"text\": \"quest started\",\n" +
                $"    \"templateId\": \"{gameTaskId} failMessageText\"\n  }}\n}}\n");
            var incremental = monitor.ReadLatest(root, replayHistoryWhenUninitialized: false);
            Assert(incremental.Count == 1 && incremental[0].Status == TaskTrackingStatus.NotStarted,
                "Incremental task failure was not applied exactly once.");
        }
        finally
        {
            Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
        }
    }

    private static void VerifyTaskProgressBackupRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tml-task-progress-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "task-progress.json");
        Directory.CreateDirectory(root);

        static Dictionary<string, Dictionary<string, ModuleTaskTrackingStatus>> Progress(
            ModuleTaskTrackingStatus pveStatus,
            bool includePvp = false) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["pvp"] = includePvp
                    ? new Dictionary<string, ModuleTaskTrackingStatus>(StringComparer.Ordinal) { ["pvp:task"] = ModuleTaskTrackingStatus.Completed }
                    : new Dictionary<string, ModuleTaskTrackingStatus>(StringComparer.Ordinal),
                ["pve"] = new Dictionary<string, ModuleTaskTrackingStatus>(StringComparer.Ordinal) { ["pve-task"] = pveStatus },
                ["season"] = new Dictionary<string, ModuleTaskTrackingStatus>(StringComparer.Ordinal)
            };

        try
        {
            Assert(ModuleTaskTrackingProgressService.SaveByModeForTest(path, Progress(ModuleTaskTrackingStatus.Accepted)),
                "Initial task progress save failed.");
            Assert(ModuleTaskTrackingProgressService.SaveByModeForTest(path, Progress(ModuleTaskTrackingStatus.Completed)),
                "Second task progress save failed.");
            Assert(ModuleTaskTrackingProgressService.SaveByModeForTest(path, Progress(ModuleTaskTrackingStatus.Completed, includePvp: true)),
                "Third task progress save failed.");

            File.WriteAllText(path, "{ damaged primary");
            var firstRecovery = ModuleTaskTrackingProgressService.LoadByModeForTest(path);
            Assert(firstRecovery["pve"].GetValueOrDefault("pve-task") == ModuleTaskTrackingStatus.Completed &&
                   !firstRecovery["pvp"].ContainsKey("pvp:task"),
                "Task progress was not recovered from the newest backup.");

            File.WriteAllText(path, "{ damaged primary again");
            File.WriteAllText($"{path}.1.bak", "{ damaged first backup");
            var secondRecovery = ModuleTaskTrackingProgressService.LoadByModeForTest(path);
            Assert(secondRecovery["pve"].GetValueOrDefault("pve-task") == ModuleTaskTrackingStatus.Accepted,
                "Task progress was not recovered from the second backup.");
            Assert(File.ReadAllText(path).Contains("\"schemaVersion\": 2", StringComparison.Ordinal),
                "Recovered task progress was not restored to the primary file.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyExtractionData()
    {
        Assert(ExtractionRequirementService.RuleCount >= 150, "Extraction rule coverage regressed.");
        Assert(ExtractionRequirementService.DataVersion == "2026.07.31", "Unexpected extraction data version.");
        Assert(ExtractionRequirementService.BuildToolTip("interchange", "extract", "河畔之路（信号弹）", "pmc").Contains("绿色信号弹", StringComparison.Ordinal), "Flare extract rule is missing.");
        Assert(ExtractionRequirementService.BuildToolTip("factory", "transit", "前往实验室", "pmc").Contains("实验室门禁卡", StringComparison.Ordinal), "Lab transit rule is missing.");
        Assert(ExtractionRequirementService.HasExplicitRule("factory", "0号门", "pmc"), "A standard Factory extract lost its explicit rule.");
        Assert(ExtractionRequirementService.HasExplicitRule("customs", "铁路通道（信号弹）", "pmc"), "Customs flare extract rule is missing.");
        Assert(ExtractionRequirementService.HasExplicitRule("customs", "走私者地堡（ZB-1012）", "pmc"), "Customs ZB-1012 extract rule is missing.");
        Assert(ExtractionRequirementService.BuildToolTip("unknown", "extract", "future-extract", "pmc").Contains("资料未覆盖", StringComparison.Ordinal), "Unknown extracts must not claim to be unconditionally open.");
    }

    private static void VerifyMapPointsAndProjection()
    {
        var first = LocalMapPointService.Load();
        Assert(ReferenceEquals(first, LocalMapPointService.Load()), "Map point cache is not reused.");
        Assert(first.MarkersByMap.Count == 13, "Point data must cover all supported maps.");
        var mapIds = MapCatalogService.Load().Maps.Select(map => map.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(
            mapIds.SetEquals(first.BoundsByMap.Keys) && mapIds.SetEquals(first.MarkersByMap.Keys),
            "Every shipped map must have matching coordinate bounds and point data.");
        foreach (var mapId in mapIds)
        {
            var projection = first.BoundsByMap[mapId];
            var centerX = (projection.X0 + projection.X1) / 2;
            var centerZ = (projection.Z0 + projection.Z1) / 2;
            Assert(
                projection.TryProject(centerX, centerZ, out var centerU, out var centerV) &&
                Math.Abs(centerU - .5) < .0001 && Math.Abs(centerV - .5) < .0001,
                $"{mapId} map projection has an invalid center transform.");
        }
        Assert(first.MarkersByMap.Values.SelectMany(value => value).All(marker => marker.X is >= 0 and <= 1 && marker.Y is >= 0 and <= 1), "A map marker is outside normalized bounds.");
        var keyRoomMarkers = first.MarkersByMap.Values.SelectMany(value => value).Where(marker => marker.Type == "key-room").ToArray();
        Assert(keyRoomMarkers.Length >= 330, "Key-room marker coverage regressed.");
        Assert(keyRoomMarkers.All(marker => !marker.ShowLabel && marker.ToolTipText?.Contains("需要钥匙", StringComparison.Ordinal) == true), "Key-room markers must remain icon-only and explain the required key.");
        Assert(first.MarkersByMap["reserve"].Any(marker => marker.Type == "key-room" && marker.Label == "RB-PKPTS 钥匙"), "Latest Reserve key room is missing.");
        var switchMarkers = first.MarkersByMap.Values.SelectMany(value => value).Where(marker => marker.Type == "switch").ToArray();
        Assert(switchMarkers.Length == 33, "Filtered Kaedeori switch marker coverage regressed.");
        Assert(switchMarkers.All(marker => marker.ShowLabel && marker.ToolTipText?.Contains("拉闸点：", StringComparison.Ordinal) == true), "Switch markers must show their names and explain their action.");
        Assert(first.MarkersByMap["customs"].Any(marker => marker.Type == "switch" && marker.Label == "ZB-013 电源拉闸"), "Customs ZB-013 power switch is missing.");
        Assert(first.MarkersByMap["reserve"].Count(marker => marker.Type == "switch") == 3, "Reserve switch marker coverage regressed.");
        var labSwitches = first.MarkersByMap["the-lab"].Where(marker => marker.Type == "switch").ToArray();
        Assert(labSwitches.Length == 9, "Labs switch marker filtering regressed.");
        Assert(labSwitches.All(marker => !marker.Label.Contains("呼叫按钮", StringComparison.Ordinal) && !marker.Label.Contains("撤离按钮", StringComparison.Ordinal)), "Labs elevator call/extract buttons must not be rendered as switch markers.");
        Assert(labSwitches.Count(marker => marker.Label.EndsWith("电梯拉闸点", StringComparison.Ordinal)) == 3, "Labs elevator power switches must use the pull-switch label.");
        var seasonDocuments = first.MarkersByMap.Values.SelectMany(value => value).Where(marker => marker.Type == "season-document").ToArray();
        Assert(seasonDocuments.Length == 350, $"Season-document marker coverage regressed: {seasonDocuments.Length}/350.");
        Assert(seasonDocuments.All(marker => marker.ShowLabel && marker.WorldX is not null && marker.WorldHeight is not null && marker.WorldZ is not null && marker.ToolTipText?.Contains("坐标：", StringComparison.Ordinal) == true),
            "Season-document markers must retain their exact world coordinates and labels.");
        Assert(seasonDocuments.All(marker =>
                marker.PreviewImageId?.Length == 32 &&
                Uri.TryCreate(marker.PreviewImageUrl, UriKind.Absolute, out var imageUri) &&
                imageUri.Scheme == Uri.UriSchemeHttps &&
                imageUri.Host.Equals("cdn.kaedeori.com", StringComparison.OrdinalIgnoreCase)),
            "Every rendered season-document marker must retain its Kaedeori location screenshot.");
        Assert(first.MarkersByMap["customs"].Any(marker =>
                marker.Type == "season-document" && marker.Label == "项目文件" &&
                Math.Abs(marker.WorldX.GetValueOrDefault() - 26.08) < .001 &&
                Math.Abs(marker.WorldHeight.GetValueOrDefault() - 5.83) < .001 &&
                Math.Abs(marker.WorldZ.GetValueOrDefault() + 57.84) < .001),
            "The verified Customs project-document coordinate is missing.");

        var customsMarkers = first.MarkersByMap["customs"];
        var zb013 = customsMarkers.Single(marker => marker.Type == "extract" && marker.Label == "ZB-013");
        var zb1011 = customsMarkers.Single(marker => marker.Type == "extract" && marker.Label == "ZB-1011");
        var zb1012 = customsMarkers.Single(marker => marker.Type == "extract" && marker.Label == "走私者地堡（ZB-1012）");
        Assert(zb013.VisibleOnSurface && zb1011.VisibleOnSurface && zb1012.VisibleOnSurface, "Customs bunker extracts must remain visible on the surface map.");
        var customsLayers = MapLayerCatalogService.Load().LayersByMap["customs"];
        Assert(MapLayerVisibilityService.IsVisible(zb013, null, customsLayers) && MapLayerVisibilityService.IsVisible(zb1011, null, customsLayers) && MapLayerVisibilityService.IsVisible(zb1012, null, customsLayers), "Customs bunker extracts are hidden by floor filtering.");
        var customsUnderground = customsLayers.Single(layer => layer.Id == "underground");
        Assert(MapLayerVisibilityService.IsVisible(zb013, customsUnderground, customsLayers) && MapLayerVisibilityService.IsVisible(zb1011, customsUnderground, customsLayers) && MapLayerVisibilityService.IsVisible(zb1012, customsUnderground, customsLayers), "Customs bunker extracts must remain visible on the underground layer.");
        Assert(customsMarkers.Any(marker => marker.Type == "extract" && marker.Label == "铁路通道（信号弹）"), "Customs flare extract label is stale.");

        var labsBounds = first.BoundsByMap["the-lab"];
        Assert(
            labsBounds == new MapCoordinateBounds(-283, -478.5, -81, -197, true, 180),
            "Labs local points must retain their calibrated projection bounds.");

        var factoryBounds = first.BoundsByMap["factory"];
        Assert(factoryBounds.PositionRotation == 90, "Factory must apply its source quarter-turn.");
        Assert(
            factoryBounds.TryProject(58.43222, 63.29811, out var factoryGate3X, out var factoryGate3Y) &&
            Math.Abs(factoryGate3X - .03109848) < .0001 &&
            Math.Abs(factoryGate3Y - .13030021) < .0001,
            "Factory Gate 3 projection is not aligned with the rendered map.");

        var bounds = new MapCoordinateBounds(100, -100, -100, 100, false, 180);
        Assert(bounds.TryProject(0, 0, out var x, out var y) && Math.Abs(x - .5) < .0001 && Math.Abs(y - .5) < .0001, "Projection center is incorrect.");
        Assert(bounds.Contains(0, 0) && !bounds.Contains(117, 0), "Strict map ownership accepted a coordinate outside source bounds.");
        Assert(double.IsFinite(bounds.ProjectHeading(137)), "Projected heading is invalid.");
    }

    private static void VerifyIncrementalCollectionNotifications()
    {
        var values = new BulkObservableCollection<int>();
        var adds = 0;
        var resets = 0;
        values.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add) adds++;
            if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
        };
        values.AddRange([1, 2, 3]);
        Assert(values.Count == 3 && adds == 3 && resets == 0, "Incremental append must not reset the virtualized list.");
    }

    private static void VerifyCoordinateStaleness()
    {
        var viewModel = new MainViewModel();
        var map = viewModel.Maps.First(candidate => candidate.WorldBounds is not null);
        viewModel.SelectedMap = map;
        var bounds = map.WorldBounds!.Value;
        var x = (bounds.X0 + bounds.X1) / 2;
        var z = (bounds.Z0 + bounds.Z1) / 2;
        var now = DateTimeOffset.Now;

        viewModel.SetLiveCoordinate(new LiveCoordinate("stale.png", x, 0, z, 90, now.AddMinutes(-4), 1), map.Id);
        Assert(viewModel.IsCoordinateStale, "Old screenshot coordinate was not marked stale.");
        Assert(viewModel.PlayerMarker?.Type == "player-stale", "Stale coordinate must remain visible as a stale player marker.");
        Assert(viewModel.GetShareableCoordinate(now) is null, "Stale coordinate must not be shared as a live teammate position.");

        viewModel.SetLiveCoordinate(new LiveCoordinate("fresh.png", x, 0, z, 90, now, 2), map.Id);
        Assert(!viewModel.IsCoordinateStale, "Fresh screenshot coordinate was incorrectly marked stale.");
        Assert(viewModel.PlayerMarker?.Type == "player", "Fresh coordinate must restore the live player marker.");

        Assert(viewModel.MarkLiveCoordinateStale(), "A confirmed point could not be explicitly marked stale.");
        Assert(viewModel.PlayerMarker?.Type == "player-stale", "An explicitly stale point must remain visible in blue/stale form.");

        viewModel.SetLiveCoordinate(new LiveCoordinate("outside.png", bounds.X0 + (bounds.X1 - bounds.X0) * 3, 0, bounds.Z0, 90, now, 3), map.Id);
        Assert(viewModel.PlayerMarker is null, "An out-of-bounds coordinate unexpectedly produced a player marker.");
        Assert(!string.IsNullOrWhiteSpace(viewModel.PlayerMarkerProjectionError), "An out-of-bounds coordinate did not expose a projection diagnostic.");

        viewModel.SetLiveCoordinate(new LiveCoordinate("future.png", x, 0, z, 90, now + LocalRaidMonitorService.CoordinateFutureTolerance + TimeSpan.FromMinutes(1), 4), map.Id);
        Assert(viewModel.IsCoordinateStale, "A future-dated coordinate was treated as fresh.");
        Assert(viewModel.GetShareableCoordinate(now) is null, "A future-dated coordinate must not be shared as a live teammate position.");
    }

    private static void VerifyAirdropTriangulation()
    {
        var now = DateTimeOffset.Now;
        var first = new AirdropBearingSample("test", "a.png", 0, 0, 90, now, 1);
        var second = new AirdropBearingSample("test", "b.png", 10, -10, 0, now.AddSeconds(5), 2);
        Assert(
            AirdropTriangulationService.TryEstimate(first, second, out var estimate, out var failure) &&
            estimate is not null,
            $"Valid airdrop triangulation failed: {failure}");
        Assert(Math.Abs(estimate!.X - 10) < .0001 && Math.Abs(estimate.Z) < .0001, "Airdrop intersection is incorrect.");
        Assert(Math.Abs(estimate.CrossingAngleDegrees - 90) < .0001, "Airdrop crossing angle is incorrect.");

        var behind = second with { Z = 10, FileName = "behind.png" };
        Assert(!AirdropTriangulationService.TryEstimate(first, behind, out _, out _), "Behind-player intersection must be rejected.");

        var bounds = new MapCoordinateBounds(0, 0, 100, 100, false, 0);
        var mapBearing = first with { X = 20, Z = 30 };
        Assert(AirdropTriangulationService.TryBuildMapRay(bounds, mapBearing, out var ray) && ray is not null, "Airdrop map ray could not be projected.");
        Assert(Math.Abs(ray!.StartX - .2) < .0001 && Math.Abs(ray.StartY - .3) < .0001, "Airdrop map ray start is incorrect.");
        Assert(Math.Abs(ray.EndX - 1) < .0001 && Math.Abs(ray.EndY - .3) < .0001, "Airdrop map ray endpoint is incorrect.");

        var quarterTurnBounds = new MapCoordinateBounds(0, 0, 100, 100, false, 0, 90);
        Assert(
            AirdropTriangulationService.TryBuildMapRay(quarterTurnBounds, mapBearing, out var turnedRay) &&
            turnedRay is not null,
            "Quarter-turned airdrop map ray could not be projected.");
        Assert(
            Math.Abs(turnedRay!.StartX - .7) < .0001 &&
            Math.Abs(turnedRay.StartY - .2) < .0001 &&
            Math.Abs(turnedRay.EndX - .7) < .0001 &&
            Math.Abs(turnedRay.EndY - 1) < .0001,
            "Quarter-turned map ray is not aligned with the rotated marker projection.");
    }

    private static void VerifyIncrementalLogReader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-log-regression-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "2026-07-22 10:00:00|application|GameStarted\npartial", Encoding.UTF8);
            var first = LocalRaidMonitorService.ReadLogChunk(path, 0L, 1024 * 1024, false);
            Assert(first.Text.Contains("GameStarted", StringComparison.Ordinal) && !first.Text.Contains("partial", StringComparison.Ordinal), "Incomplete log line was processed.");

            File.AppendAllText(path, " line\n", Encoding.UTF8);
            var second = LocalRaidMonitorService.ReadLogChunk(path, first.NextOffset, 1024 * 1024, false);
            Assert(second.Text == "partial line\n", "Incremental log reader did not resume at the last complete line.");

            var expectedLines = string.Concat(Enumerable.Range(0, 240).Select(index => $"2026-07-22 10:00:{index % 60:00}|application|line-{index}\n"));
            File.WriteAllText(path, expectedLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var offset = 0L;
            var actualLines = new StringBuilder();
            var length = new FileInfo(path).Length;
            while (offset < length)
            {
                var chunk = LocalRaidMonitorService.ReadLogChunk(path, offset, 47, false);
                Assert(chunk.NextOffset > offset, "Bounded log reads must advance instead of jumping to the file tail.");
                actualLines.Append(chunk.Text);
                offset = chunk.NextOffset;
            }
            Assert(actualLines.ToString() == expectedLines, "Bounded log reads skipped or duplicated log records.");

            File.WriteAllText(path, new string('x', 80) + "\nvalid\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var oversized = LocalRaidMonitorService.ReadLogChunk(path, 0, 64, false);
            Assert(oversized.Text.Length == 0 && oversized.NextOffset == 64 && oversized.SkippedPartialLine,
                "An oversized log record did not advance within the bounded read budget.");
            var resumed = LocalRaidMonitorService.ReadLogChunk(path, oversized.NextOffset, 64, false);
            Assert(resumed.Text == "valid\n" && resumed.NextOffset == new FileInfo(path).Length && resumed.SkippedPartialLine,
                "The log reader did not resume at the first complete record after an oversized line.");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void VerifyMapTokenRecognition()
    {
        AssertResolvedMap(
            "2026-07-31 14:14:35.758|Info|application|scene preset path:maps/unrecognized_preset.bundle rcid:city.scenespreset.asset",
            "streets-of-tarkov",
            "Street scene rcid was not recognized.");
        AssertResolvedMap(
            "2026-07-31 14:17:00.457|Debug|application|TRACE-NetworkGameCreate profileStatus: 'Status: Busy, Location: TarkovStreets, Sid: test'",
            "streets-of-tarkov",
            "Street NetworkGame location was not recognized.");
        AssertResolvedMap(
            "2026-07-31 14:14:35.758|Info|application|scene preset path:maps/city_preset.bundle",
            "streets-of-tarkov",
            "Street city_preset bundle was not recognized.");
        AssertResolvedMap(
            "2026-08-08 20:00:00.000|Info|application|scene preset path:maps/shopping_mall.bundle rcid:Shopping_Mall.ScenesPreset.asset",
            "interchange",
            "Interchange shopping_mall scene was not recognized.");
        AssertResolvedMap(
            "2026-08-08 20:00:00.000|Info|application|scene preset path:maps/factory_day_preset.bundle rcid:factory_day.scenespreset.asset",
            "factory",
            "Factory scene alias was not recognized.");
        AssertResolvedMap(
            "2026-08-08 20:00:00.000|Info|application|scene preset path:maps/laboratory_dark_preset.bundle rcid:laboratory_dark.ScenesPreset.asset",
            "the-labyrinth",
            "Labyrinth scene alias was not recognized.");
        AssertResolvedMap(
            "2026-08-14 20:00:00.000|Info|application|scene preset path:maps/icebreaker_preset.bundle rcid:Icebreaker.ScenesPreset.asset",
            "icebreaker",
            "Icebreaker scene alias was not recognized.");
        AssertResolvedMap(
            "2026-09-07 20:48:05.925|Info|application|scene preset path:maps/rezerv_base_preset.bundle",
            "reserve",
            "Reserve scene alias was not recognized.");
        AssertResolvedMap(
            "2026-09-07 20:48:05.925|Info|application|scene preset path:maps/rezerv-base-preset.bundle",
            "reserve",
            "Hyphenated Reserve scene alias was not recognized.");
        AssertResolvedMap(
            "2026-09-07 20:48:05.925|Info|application|scene preset path:maps/unknown.bundle rcid:Rezerv_Base.ScenesPreset.asset",
            "reserve",
            "Reserve rcid alias was not normalized.");
        AssertResolvedMap(
            "2026-09-07 20:48:05.925|Info|application|scene preset path:maps/woods_preset.bundle",
            "woods",
            "Preset suffix fallback did not recognize a known map.");
        AssertResolvedMap(
            "2026-08-14 20:00:00.000|Info|application|TRACE-NetworkGameCreate profileStatus: 'Status: Busy, Location: Terminal, Sid: test'",
            "terminal",
            "Terminal NetworkGame location was not recognized.");
        AssertResolvedMap(
            "2026-08-08 20:00:00.000|Info|application|[Transit] Locations:Woods -> Customs",
            "customs",
            "Transit must select the destination map when the log supplies one.");
        AssertResolvedMapNotPending(
            "2026-08-08 20:00:00.000|Info|application|[Transit] Locations:Woods -> ",
            "woods",
            "A normal source-only route record must not leave map evidence permanently pending.");
        AssertResolvedMapNotPending(
            "2026-08-10 14:40:07.900|1.1.0.0.46657|Info|application|[Transit] Flag:Common, RaidId:test, Count:0, Locations:Interchange -> \r\n" +
            "2026-08-10 14:40:08.236|1.1.0.0.46657|Info|application|GameStarted:10 real:10 diff:0\r\n",
            "interchange",
            "A source-only route followed by another log record must not consume that record's date as a destination.");
        AssertUnresolvedMap(
            "2026-08-08 20:00:00.000|Info|application|diagnostic Location: Woods, cache cleanup complete",
            "An unrelated Location field must not switch the active map.");
        AssertUnresolvedMap(
            "2026-09-07 20:48:05.925|Info|application|scene preset path:maps/future_map_preset.bundle",
            "An unknown normalized scene must not be mistaken for a supported map.");
        var unsupported = LocalRaidMonitorService.ResolveLatestMapToken(
            "2026-08-08 20:00:00.000|Info|application|TRACE-NetworkGameCreate profileStatus: 'Status: Busy, Location: FutureMap, Sid: test'");
        Assert(unsupported.MapKey is null && unsupported.InvalidatesExistingMap,
            "An explicit unsupported NetworkGame map must invalidate stale map evidence.");
    }

    private static void VerifyRaidEndAndUnsupportedMapInvalidation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-raid-end-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "Logs", "log_2026.08.20_20-00-00_127.0.0.1");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "application.log");
        try
        {
            File.WriteAllText(logPath,
                "2026-08-20 20:00:00.000|Info|application|scene preset path:maps/laboratory_preset.bundle rcid:laboratory.ScenesPreset.asset\n" +
                "2026-08-20 20:00:05.000|Info|application|GameStarted:5 real:5 diff:0\n",
                new UTF8Encoding(false));
            using var monitor = new LocalRaidMonitorService();
            var started = monitor.ReadLatestMap(root);
            Assert(string.Equals(started.MapKey, "the-lab", StringComparison.OrdinalIgnoreCase) && started.RaidEndAt is null,
                "Raid start fixture did not establish an active laboratory raid.");

            File.AppendAllText(logPath,
                "2026-08-20 20:05:00.000|Info|application|PrepareSelectedProfileLocally\n",
                new UTF8Encoding(false));
            var ended = monitor.ReadLatestMap(root);
            Assert(ended.RaidEndAt is not null, "The live raid-end marker did not close the active raid.");

            File.AppendAllText(logPath,
                "2026-08-20 20:06:00.000|Info|application|TRACE-NetworkGameCreate profileStatus: 'Status: Busy, Location: FutureMap, Sid: test'\n",
                new UTF8Encoding(false));
            var unsupported = monitor.ReadLatestMap(root);
            Assert(unsupported.MapKey is null, "An unsupported explicit scene left the previous laboratory map active.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyMapMonitorEvidenceSafety()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-large-log-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "Logs", "log_2026.08.08_20-00-00_127.0.0.1");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "application.log");
        try
        {
            const int initialReadBudget = 8 * 1024 * 1024;
            var text = new StringBuilder("2026-08-08 20:00:00.000|Info|application|scene preset path:maps/woods.bundle\n");
            var filler = new string('x', 1022) + "\n";
            while (text.Length < initialReadBudget + 256 * 1024) text.Append(filler);
            text.Append("2026-08-08 20:10:00.000|Info|application|scene preset path:maps/customs.bundle\n");
            File.WriteAllText(logPath, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var monitor = new LocalRaidMonitorService();
            var first = monitor.ReadLatestMap(root);
            Assert(
                string.Equals(first.MapKey, "woods", StringComparison.OrdinalIgnoreCase) &&
                !first.IsMapLogCaughtUp,
                "A partial initial read must be explicitly marked as historical/unconfirmed.");

            var second = first;
            for (var attempt = 0; attempt < 8 && !second.IsMapLogCaughtUp; attempt++)
                second = monitor.ReadLatestMap(root);
            Assert(
                string.Equals(second.MapKey, "customs", StringComparison.OrdinalIgnoreCase) && second.IsMapLogCaughtUp,
                $"The monitor did not reach the newest map after completing the bounded catch-up read. Map={second.MapKey ?? "none"}; CaughtUp={second.IsMapLogCaughtUp}.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyOrdinaryRouteRecordDoesNotBlockCoordinates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-route-log-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "Logs", "log_2026.08.08_20-00-00_127.0.0.1");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "application.log");
        try
        {
            File.WriteAllText(
                logPath,
                "2026-08-08 20:00:00.000|1.1.0|Info|application|scene preset path:maps/shopping_mall.bundle rcid:Shopping_Mall.ScenesPreset.asset\n" +
                "2026-08-08 20:00:05.000|1.1.0|Info|application|[Transit] Flag:Common, RaidId:test, Count:0, Locations:Interchange -> \n" +
                "2026-08-08 20:00:10.000|1.1.0|Info|application|GameStarted:10 real:10 diff:0\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var monitor = new LocalRaidMonitorService();
            var result = monitor.ReadLatestMap(root);
            Assert(string.Equals(result.MapKey, "interchange", StringComparison.OrdinalIgnoreCase),
                "A real ordinary Interchange route sequence lost its selected map.");
            Assert(result.IsMapLogCaughtUp, "The ordinary route fixture did not finish reading its log.");
            Assert(!result.IsTransitDestinationPending,
                "A source-only route emitted before GameStarted incorrectly blocked all later coordinates.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifySourceOnlyRouteCannotOverrideConfirmedScene()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-conflicting-route-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "Logs", "log_2026.08.19_18-19-48_1.1.0.1.46777");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "application.log");
        try
        {
            // This is the real ordering that caused an intermittent Lab ->
            // Labyrinth switch on 2026-08-19.  The scene and route are written
            // far enough apart to be consumed by separate monitor polls.
            File.WriteAllText(
                logPath,
                "2026-08-19 18:28:27.268|1.1.0.1.46777|Info|application|scene preset path:maps/laboratory_preset.bundle rcid:laboratory.ScenesPreset.asset\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var monitor = new LocalRaidMonitorService();
            var sceneResult = monitor.ReadLatestMap(root);
            Assert(
                string.Equals(sceneResult.MapKey, "the-lab", StringComparison.OrdinalIgnoreCase),
                "The laboratory scene was not established before the conflicting route fixture.");

            File.AppendAllText(
                logPath,
                "2026-08-19 18:28:49.356|1.1.0.1.46777|Info|application|[Transit] Flag:Common, RaidId:6a858553b0cf3afc290e0367, Count:0, Locations:laboratory_dark -> \n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var routeResult = monitor.ReadLatestMap(root);
            Assert(
                string.Equals(routeResult.MapKey, "the-lab", StringComparison.OrdinalIgnoreCase),
                $"A source-only laboratory_dark route overrode the confirmed laboratory scene. Map={routeResult.MapKey ?? "none"}.");
            Assert(
                routeResult.MapSource?.StartsWith("日志场景 ", StringComparison.OrdinalIgnoreCase) == true,
                $"The protected scene was replaced by weak route evidence. Source={routeResult.MapSource ?? "none"}.");
            Assert(!routeResult.IsTransitDestinationPending,
                "A pre-GameStarted source-only route with a recent confirmed scene was incorrectly marked as an active transfer.");

            File.AppendAllText(
                logPath,
                "2026-08-19 18:29:26.333|1.1.0.1.46777|Info|application|GameStarted:37.26 real:59.76 diff:22.5\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var startedResult = monitor.ReadLatestMap(root);
            Assert(
                string.Equals(startedResult.MapKey, "the-lab", StringComparison.OrdinalIgnoreCase),
                "GameStarted did not preserve the confirmed laboratory map after the conflicting route.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyActiveRaidSourceOnlyRouteWaitsForTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-active-transit-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "Logs", "log_2026.08.08_20-00-00_127.0.0.1");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "application.log");
        try
        {
            File.WriteAllText(
                logPath,
                "2026-08-08 20:00:00.000|1.1.0|Info|application|scene preset path:maps/shopping_mall.bundle rcid:Shopping_Mall.ScenesPreset.asset\n" +
                "2026-08-08 20:00:05.000|1.1.0|Info|application|[Transit] Flag:Common, RaidId:test, Count:0, Locations:Interchange -> \n" +
                "2026-08-08 20:00:10.000|1.1.0|Info|application|GameStarted:10 real:10 diff:0\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var monitor = new LocalRaidMonitorService();
            var initial = monitor.ReadLatestMap(root);
            Assert(!initial.IsTransitDestinationPending, "The ordinary pre-GameStarted route was incorrectly marked pending.");

            File.AppendAllText(
                logPath,
                "2026-08-08 20:05:00.000|1.1.0|Info|application|[Transit] Flag:Common, RaidId:test, Count:0, Locations:Interchange -> \n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var pending = monitor.ReadLatestMap(root);
            Assert(string.Equals(pending.MapKey, "interchange", StringComparison.OrdinalIgnoreCase), "Pending transfer discarded the last confirmed source map.");
            Assert(pending.IsTransitDestinationPending, "A source-only route emitted during an active raid did not wait for target evidence.");

            File.AppendAllText(
                logPath,
                "2026-08-08 20:05:01.000|1.1.0|Info|application|scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var resolved = monitor.ReadLatestMap(root);
            Assert(string.Equals(resolved.MapKey, "customs", StringComparison.OrdinalIgnoreCase), "Positive destination evidence did not complete the active transfer.");
            Assert(!resolved.IsTransitDestinationPending, "Transfer remained pending after a supported destination scene appeared.");

            File.AppendAllText(
                logPath,
                "2026-08-08 20:10:00.000|1.1.0|Info|application|[Transit] Flag:Common, RaidId:test, Count:0, Locations:bigmap -> \n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var secondPending = monitor.ReadLatestMap(root);
            Assert(secondPending.IsTransitDestinationPending, "Old destination evidence incorrectly authorized a later source-only transfer.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyScreenshotMonitorDetectsNewCoordinate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-screenshots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstName = "2026-08-08[20-00]_1.00, 2.00, 3.00_0.00, 0.00, 0.00, 1.00_1.00 (0).png";
            var secondName = "2026-08-08[20-01]_4.00, 5.00, 6.00_0.00, 0.00, 0.00, 1.00_1.00 (0).png";
            File.WriteAllBytes(Path.Combine(root, firstName), []);

            using var monitor = new LocalRaidMonitorService();
            var first = monitor.ReadLatestCoordinate(root);
            Assert(first?.FileName == firstName, "The initial screenshot scan did not select its coordinate file.");
            var embeddedTimestamp = new DateTimeOffset(new DateTime(2026, 8, 8, 20, 0, 0, DateTimeKind.Local));
            Assert(first?.FileNameTimestamp == embeddedTimestamp, "The screenshot filename timestamp was not retained.");
            Assert(first?.CapturedAt == embeddedTimestamp.AddSeconds(30), "A copied screenshot incorrectly used its new filesystem write time.");

            File.WriteAllBytes(Path.Combine(root, secondName), []);
            LiveCoordinate? latest = null;
            for (var attempt = 0; attempt < 20 && latest?.FileName != secondName; attempt++)
            {
                Thread.Sleep(25);
                latest = monitor.ReadLatestCoordinate(root);
            }
            Assert(latest?.FileName == secondName,
                "A newly created screenshot was not detected by watcher or directory-timestamp recovery.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyScreenshotMonitorForcedRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-screenshot-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstName = "2026-08-08[20-00]_1.00, 2.00, 3.00_0.00, 0.00, 0.00, 1.00_1.00 (0).png";
            var secondName = "2026-08-08[20-00]_4.00, 5.00, 6.00_0.00, 0.00, 0.00, 1.00_1.00 (0).png";
            var firstPath = Path.Combine(root, firstName);
            var secondPath = Path.Combine(root, secondName);
            File.WriteAllBytes(firstPath, []);
            File.SetLastWriteTime(firstPath, new DateTime(2026, 8, 8, 20, 0, 10, DateTimeKind.Local));

            using var monitor = new LocalRaidMonitorService();
            Assert(monitor.ReadLatestCoordinate(root)?.FileName == firstName,
                "The recovery test could not establish its initial coordinate.");

            File.WriteAllBytes(secondPath, []);
            File.SetLastWriteTime(secondPath, new DateTime(2026, 8, 8, 20, 0, 20, DateTimeKind.Local));
            monitor.RequestScreenshotRescan(restartWatcher: true);
            var recovered = monitor.ReadLatestCoordinate(root);
            Assert(recovered?.FileName == secondName,
                "A forced watcher restart and directory reconciliation did not recover a newer same-minute screenshot.");

            var future = DateTime.Now + LocalRaidMonitorService.CoordinateFutureTolerance + TimeSpan.FromMinutes(1);
            var futureName = $"{future:yyyy-MM-dd}[{future:HH-mm}]_7.00, 8.00, 9.00_0.00, 0.00, 0.00, 1.00_1.00 (0).png";
            File.WriteAllBytes(Path.Combine(root, futureName), []);
            monitor.RequestScreenshotRescan();
            Assert(monitor.ReadLatestCoordinate(root)?.FileName == secondName,
                "A future-dated screenshot displaced the latest plausible coordinate.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyTransferCoordinateIsolation()
    {
        var now = DateTimeOffset.Now;
        var beforeTransfer = new LiveCoordinate("old.png", 1, 2, 3, 0, now - TimeSpan.FromSeconds(20), 1);
        var afterTransfer = beforeTransfer with { FileName = "new.png", CapturedAt = now - TimeSpan.FromSeconds(1), SortOrder = 2 };
        var delayedWrite = beforeTransfer with { FileName = "delayed.png", CapturedAt = now - TimeSpan.FromSeconds(8), SortOrder = 3 };
        var staleSnapshot = new LocalRaidSnapshot(
            "customs", "transit", "test", beforeTransfer, "test", true, "screens", "logs", "application.log",
            now - TimeSpan.FromMinutes(10), null, now);
        var freshSnapshot = staleSnapshot with { Coordinate = afterTransfer };

        Assert(!TarkovMapLocatorDesktop.MainWindow.IsCoordinateFromCurrentRaid(staleSnapshot), "A cached screenshot from before a transfer was accepted for the destination map.");
        Assert(TarkovMapLocatorDesktop.MainWindow.IsCoordinateFromCurrentRaid(freshSnapshot), "A screenshot taken after a transfer was rejected for the destination map.");
        Assert(TarkovMapLocatorDesktop.MainWindow.IsCoordinateFromCurrentRaid(staleSnapshot with { Coordinate = delayedWrite }), "A screenshot within the bounded log/write delay tolerance was rejected.");
        Assert(!TarkovMapLocatorDesktop.MainWindow.IsCoordinateFromCurrentRaid(staleSnapshot with
        {
            Coordinate = afterTransfer with { CapturedAt = now + LocalRaidMonitorService.CoordinateFutureTolerance + TimeSpan.FromMinutes(1) }
        }), "A future-dated screenshot was accepted for the current raid.");
    }

    private static void AssertResolvedMap(string logText, string expectedMapKey, string message)
    {
        var result = LocalRaidMonitorService.ResolveLatestMapToken(logText);
        Assert(string.Equals(result.MapKey, expectedMapKey, StringComparison.OrdinalIgnoreCase) && result.RecordCount > 0, message);
    }

    private static void AssertResolvedMapNotPending(string logText, string expectedMapKey, string message)
    {
        var result = LocalRaidMonitorService.ResolveLatestMapToken(logText);
        Assert(string.Equals(result.MapKey, expectedMapKey, StringComparison.OrdinalIgnoreCase) && !result.IsTransitDestinationPending, message);
    }

    private static void AssertUnresolvedMap(string logText, string message)
    {
        var result = LocalRaidMonitorService.ResolveLatestMapToken(logText);
        Assert(string.IsNullOrWhiteSpace(result.MapKey), message);
    }

    private static void VerifyMapImageDoesNotUpscale()
    {
        var viewModel = new MainViewModel();
        if (viewModel.CurrentMapImage is null)
        {
            using var loaded = new ManualResetEventSlim();
            viewModel.MapImageLoaded += _ => loaded.Set();
            Assert(loaded.Wait(TimeSpan.FromSeconds(8)), "Map image did not finish background loading.");
        }

        var map = viewModel.SelectedMap ?? throw new InvalidOperationException("No selected map.");
        var image = viewModel.CurrentMapImage as BitmapSource ?? throw new InvalidOperationException("Map image is unavailable.");
        using var stream = File.OpenRead(map.ImageFilePath!);
        var original = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        Assert(image.PixelWidth == Math.Min(original.PixelWidth, 4096), "Small map image was upscaled during decode.");
    }

    private static void VerifyLargeTrackerBudget()
    {
        var item = new TrackerItem("test", "测试", "测试", "", 100_000, 0, 0, 0, 0, 100_000, 0, 0, 100_000, null, [], false, []);
        Assert(item.EstimatedCost == 10_000_000_000L, "Tracker budget overflowed 32-bit arithmetic.");
    }

    private static void VerifyMarketCatalogCompleteness()
    {
        var item = new MarketItem("test-item", "测试物品", "测试", "Test item", "Test", null, null, null, null, null, null, []);
        var partial = new MarketCatalog([item], [], DateTimeOffset.Now, "test", null);
        var complete = new MarketCatalog([item], [item], DateTimeOffset.Now, "test", null, [item]);
        Assert(partial.IsAvailable && !partial.IsComplete, "Single-mode market cache must remain visible but be considered incomplete.");
        Assert(complete.IsAvailable && complete.IsComplete, "PVP/PVE/seasonal market cache must be complete only when all three economies exist.");
    }

    private static void VerifyJsonApiMarketParser()
    {
        var itemsPayload = Encoding.UTF8.GetBytes("""
            {
              "data": {
                "items": {
                  "item-1": {
                    "id": "item-1",
                    "name": "item-1 Name",
                    "shortName": "item-1 ShortName",
                    "normalizedName": "english item",
                    "avg24hPrice": 1200,
                    "low24hPrice": 900,
                    "high24hPrice": 1600,
                    "lastLowPrice": 1000,
                    "basePrice": 800,
                    "iconLink": "https://example.test/icon.png",
                    "gridImageLink": "https://example.test/grid.png",
                    "types": ["barter_item"],
                    "width": 2,
                    "height": 1,
                    "sellToTrader": [
                      { "trader": "trader-1", "price": 13, "priceRUB": 1300, "currency": "USD" }
                    ],
                    "buyFromTrader": [
                      { "trader": "trader-1", "price": 700, "priceRUB": 700, "currency": "RUB", "minTraderLevel": 2 }
                    ]
                  }
                }
              }
            }
            """);
        var chinesePayload = Encoding.UTF8.GetBytes("""
            { "data": { "item-1 Name": "中文物品", "item-1 ShortName": "中物" } }
            """);
        var englishPayload = Encoding.UTF8.GetBytes("""
            { "data": { "item-1 Name": "English item", "item-1 ShortName": "English" } }
            """);

        var items = MarketPriceService.ParseJsonApiMarketItems(
            itemsPayload,
            chinesePayload,
            englishPayload,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["trader-1"] = "商人一号" });
        Assert(items.Count == 1, "JSON API item parser did not return the fixture item.");
        var item = items[0];
        Assert(item.NameZh == "中文物品" && item.ShortNameZh == "中物", "JSON API Chinese translation map was not applied.");
        Assert(item.Name == "English item" && item.ShortName == "English", "JSON API English translation map was not applied.");
        Assert(item.FleaPrice == 1000, "JSON API flea price was not mapped from lastLowPrice.");
        Assert(item.BasePrice == 800, "JSON API base price required by fee calculation was not preserved.");
        Assert(item.BestTraderBuy is { Price: 700, VendorName: "商人一号", MinTraderLevel: 2 }, "JSON API trader purchase offer was not preserved.");
        Assert(item.BestTrader is { Price: 1300, VendorName: "商人一号" }, "JSON API trader sell price was not normalized to RUB.");
        Assert(item.SellFor.Any(offer => offer.Source == "fleaMarket" && offer.Price == 1000), "JSON API flea offer was not synthesized for the existing price detail UI.");
        var catalog = new MarketCatalog(items, items, DateTimeOffset.Now, "https://json.tarkov.dev", null, [item]);
        Assert(MarketPriceService.IsJsonApiCatalog(catalog), "JSON API catalog source was not recognized.");
        Assert(catalog.GetItems("pvp-season").Single().Id == item.Id, "Seasonal market items were not selected by the pvp-season mode.");
    }

    private static void VerifyInGamePriceRecognitionSafety()
    {
        var redCard = new FeatureMarketItem(
            "red-card",
            "实验室红色钥匙卡",
            "红卡",
            "TerraGroup Labs keycard (Red)",
            "Red",
            1,
            1,
            1,
            1,
            1,
            null,
            [],
            "",
            "",
            [],
            null,
            null);
        var thermal = new FeatureMarketItem(
            "thermal",
            "T7 热成像目镜",
            "T7",
            "T-7 Thermal Goggles",
            "T7",
            1,
            1,
            1,
            1,
            1,
            null,
            [],
            "",
            "",
            [],
            null,
            null);
        var index = new InGamePriceLookupIndex([redCard, thermal]);

        Assert(index.FindBest("re", .72) is null, "Short OCR text must not fuzzy-match a Red keycard.");
        Assert(index.FindBest("red", .72) is { Item.Id: "red-card", IsExact: true }, "Unique exact aliases must remain immediately usable.");
        Assert(index.FindBest("T7", .72) is null, "Short display aliases must not bypass OCR confirmation.");
        var fuzzy = index.FindBest("thermalgogles", .72);
        Assert(fuzzy is { Item.Id: "thermal", IsExact: false }, "A sufficiently strong longer OCR typo should remain matchable.");

        var duplicateOne = thermal with { Id = "duplicate-one", Name = "Duplicate one", ShortName = "KEY" };
        var duplicateTwo = thermal with { Id = "duplicate-two", Name = "Duplicate two", ShortName = "KEY" };
        var duplicateIndex = new InGamePriceLookupIndex([duplicateOne, duplicateTwo]);
        Assert(duplicateIndex.FindBest("KEY", .72) is null, "Ambiguous exact aliases must not select an arbitrary item.");

        var gate = new InGamePriceMatchGate();
        Assert(gate.Accept(fuzzy) is null, "A fuzzy match must wait for a confirmation frame.");
        Assert(gate.Accept(fuzzy) is { Item.Id: "thermal" }, "A stable fuzzy match was not accepted on the second frame.");
        gate.Reset();
        var exact = index.FindBest("red", .72);
        Assert(gate.Accept(exact) is { Item.Id: "red-card" }, "Exact matches must not wait for a second frame.");

        var traderText = InGamePriceOverlayFormatter.BuildText(new InGamePriceMatch(
            redCard with { BestTrader = new FeatureTraderOffer(850_500, "Therapist") },
            "red",
            1));
        Assert(traderText.Contains("最高商人：大妈", StringComparison.Ordinal), "OCR overlay did not localize the English trader name.");
        Assert(InGamePriceOverlayFormatter.LocalizeTraderName("Mechanic") == "机械师", "OCR trader localization regressed for Mechanic.");
        Assert(InGamePriceOverlayFormatter.LocalizeTraderName("商人一号") == "商人一号", "Existing localized trader names must remain unchanged.");

        var legacySeed = InGamePriceRecognitionSettings.Default with
        {
            CaptureRegion = new ScreenCaptureRegion(200, 160, 960, 720)
        };
        var migratedSeed = InGamePriceView.NormalizeMainOcrSettings(legacySeed);
        Assert(
            migratedSeed.CaptureRegion is null && !migratedSeed.IsCaptureRegionUserSelected,
            "Any legacy OCR region without an explicit user-selection flag must migrate back to mouse-near automatic scanning.");

        var userSelectedArea = legacySeed with
        {
            CaptureRegion = new ScreenCaptureRegion(200, 160, 960, 720),
            IsCaptureRegionUserSelected = true
        };
        var preservedArea = InGamePriceView.NormalizeMainOcrSettings(userSelectedArea);
        Assert(
            preservedArea.CaptureRegion == userSelectedArea.CaptureRegion && preservedArea.IsCaptureRegionUserSelected,
            "A recognition area explicitly selected by the user must be preserved.");
        var customized = userSelectedArea with
        {
            OverlayRelativeX = 42,
            OverlayRelativeY = 88
        };
        var preservedCustomization = InGamePriceView.NormalizeMainOcrSettings(customized);
        Assert(
            preservedCustomization.OverlayRelativeX == customized.OverlayRelativeX &&
            preservedCustomization.OverlayRelativeY == customized.OverlayRelativeY,
            "Starting automatic OCR must not erase persisted overlay placement.");

        var settings = InGamePriceRecognitionSettings.Default with
        {
            OverlayRelativeX = 350,
            OverlayRelativeY = -18,
            OverlayWidth = 380,
            OverlayHeight = 175
        };
        var legacyCrampedTag = (settings with { OverlayWidth = 307, OverlayHeight = 106 }).Normalize();
        Assert(
            legacyCrampedTag.OverlayWidth == 380 && legacyCrampedTag.OverlayHeight == 175,
            "Legacy OCR price tags must be enlarged with a five-line safety margin.");
        var rightMonitor = InGamePriceOverlayPlacement.Resolve(settings, 3840, 216);
        var leftMonitor = InGamePriceOverlayPlacement.Resolve(settings, -1920, 216);
        Assert(rightMonitor == new InGamePriceOverlayBounds(4190, 198, 380, 175), "Overlay placement must retain physical pixels on a right-hand monitor.");
        Assert(leftMonitor == new InGamePriceOverlayBounds(-1570, 198, 380, 175), "Overlay placement must retain negative physical coordinates on a left-hand monitor.");
    }

    private static void VerifyEftCaptureTargetSelection()
    {
        var bounds = new ScreenCaptureRegion(100, 200, 1000, 700);
        var inGameCursor = new System.Drawing.Point(640, 520);
        var debugWindowCursor = new System.Drawing.Point(1500, 900);
        var remembered = EftCaptureTargetService.ResolveSearchPoint(bounds, debugWindowCursor, inGameCursor);
        Assert(remembered == inGameCursor, "OCR capture must keep the last in-game cursor after the debug window gains focus.");
        var centered = EftCaptureTargetService.ResolveSearchPoint(bounds, debugWindowCursor, null);
        Assert(centered == new System.Drawing.Point(600, 550), "OCR capture fallback must stay centered inside the EFT window.");

    }

    private static void VerifyJsonApiTaskTrackerParser()
    {
        var tasksPayload = Encoding.UTF8.GetBytes("""
            {
              "data": {
                "tasks": {
                  "task-one": {
                    "id": "task-one",
                    "name": "task-one-name",
                    "normalizedName": "Task One",
                    "trader": "trader-one",
                    "minPlayerLevel": 15,
                    "objectives": [
                      {
                        "id": "objective-one",
                        "description": "objective-one-description",
                        "type": "giveItem",
                        "count": 3,
                        "optional": false,
                        "foundInRaid": true,
                        "items": ["item-one"]
                      }
                    ]
                  },
                  "task-choice": {
                    "id": "task-choice",
                    "name": "task-choice-name",
                    "normalizedName": "Task Choice",
                    "trader": "trader-one",
                    "objectives": [
                      {
                        "id": "objective-choice",
                        "description": "objective-choice-description",
                        "type": "giveItem",
                        "count": 2,
                        "optional": false,
                        "foundInRaid": false,
                        "items": ["item-two", "item-three"]
                      }
                    ]
                  }
                }
              }
            }
            """);
        var tasksZhPayload = Encoding.UTF8.GetBytes("""
            {
              "data": {
                "task-one-name": "Task One CN",
                "task-choice-name": "Task Choice CN",
                "objective-one-description": "Bring one item",
                "objective-choice-description": "Choose one item"
              }
            }
            """);
        var hideoutPayload = Encoding.UTF8.GetBytes("""
            {
              "data": {
                "station-one": {
                  "id": "station-one",
                  "name": "station-one-name",
                  "normalizedName": "Station One",
                  "levels": [
                    {
                      "level": 2,
                      "itemRequirements": [
                        {
                          "item": "item-four",
                          "count": 4,
                          "attributes": [{ "name": "foundInRaid", "value": "true" }]
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """);
        var hideoutZhPayload = Encoding.UTF8.GetBytes("""
            { "data": { "station-one-name": "Station One CN" } }
            """);
        var itemsPayload = Encoding.UTF8.GetBytes("""
            {
              "data": {
                "items": {
                  "item-one": { "id": "item-one", "name": "item-one-name", "shortName": "item-one-short", "normalizedName": "Item One", "iconLink": "https://example.test/one-icon.png", "gridImageLink": "https://example.test/one-grid.png" },
                  "item-two": { "id": "item-two", "name": "item-two-name", "shortName": "item-two-short", "normalizedName": "Item Two" },
                  "item-three": { "id": "item-three", "name": "item-three-name", "shortName": "item-three-short", "normalizedName": "Item Three" },
                  "item-four": { "id": "item-four", "name": "item-four-name", "shortName": "item-four-short", "normalizedName": "Item Four" }
                }
              }
            }
            """);
        var itemsZhPayload = Encoding.UTF8.GetBytes("""
            {
              "data": {
                "item-one-name": "Item One CN", "item-one-short": "I1",
                "item-two-name": "Item Two CN", "item-two-short": "I2",
                "item-three-name": "Item Three CN", "item-three-short": "I3",
                "item-four-name": "Item Four CN", "item-four-short": "I4"
              }
            }
            """);
        var tradersPayload = Encoding.UTF8.GetBytes("""
            { "data": { "trader-one": { "id": "trader-one", "name": "trader-one-name", "normalizedName": "Trader One" } } }
            """);
        var tradersZhPayload = Encoding.UTF8.GetBytes("""
            { "data": { "trader-one-name": "Trader One CN" } }
            """);

        var requirements = TaskItemTrackerService.BuildJsonApiRequirementsForTest(
            tasksPayload,
            tasksZhPayload,
            hideoutPayload,
            hideoutZhPayload,
            itemsPayload,
            itemsZhPayload,
            tradersPayload,
            tradersZhPayload);

        Assert(requirements.Count == 3, "JSON task tracker parser did not emit the expected task, choice, and hideout rows.");

        var task = requirements.Single(requirement => requirement.ItemId == "item-one");
        Assert(
            task.Count == 3 && task.FoundInRaid && task.SourceName == "Task One CN" &&
            task.SourceDetail == "Bring one item" && task.Trader == "Trader One CN" && task.Level == 15 &&
            task.Item.Name == "Item One CN" && task.Item.GridImageLink == "https://example.test/one-grid.png",
            "JSON task tracker parser did not preserve translated task requirement fields.");

        var choice = requirements.Single(requirement => requirement.ItemId == "choice:objective-choice");
        Assert(
            choice.Choice && choice.Count == 2 && choice.Item.Choices is { Count: 2 } &&
            choice.Item.Choices.Select(item => item.Name).SequenceEqual(["Item Two CN", "Item Three CN"]),
            "JSON task tracker parser did not preserve one-of item choices.");

        var hideout = requirements.Single(requirement => requirement.ItemId == "item-four");
        Assert(
            hideout.SourceType == "hideout" && hideout.SourceId == "station-one" && hideout.SourceName == "Station One CN" && hideout.Level == 2 &&
            hideout.FoundInRaid && hideout.Count == 4 && hideout.Item.Name == "Item Four CN",
            "JSON task tracker parser did not preserve hideout requirements.");
        Assert(
            !TaskItemTrackerService.IsBuiltHideoutRequirementForTest(hideout, new Dictionary<string, int> { ["station-one"] = 1 }) &&
            TaskItemTrackerService.IsBuiltHideoutRequirementForTest(hideout, new Dictionary<string, int> { ["station-one"] = 2 }),
            "Hideout progress must keep the next level's materials and remove the current level's materials.");
    }

    /// <summary>Opt-in live check for the JSON task tracker migration.</summary>
    private static async Task VerifyLiveJsonTaskTrackerMigration()
    {
        var refreshed = await TaskItemTrackerService.RefreshAsync();
        var pvp = TaskItemTrackerService.Load("pvp", "", true, true, false);
        var pve = TaskItemTrackerService.Load("pve", "", true, true, false);
        var status = TaskItemTrackerService.GetCacheStatus();

        Assert(refreshed.IsAvailable && !refreshed.IsStale, "Live JSON task tracker refresh did not produce a fresh PVP list.");
        Assert(status is { IsAvailable: true, IsStale: false }, "Live JSON task tracker cache was not marked as fresh.");
        Assert(pvp.Items.Count > 50 && pve.Items.Count > 50, "Live JSON task tracker did not build both PVP and PVE item lists.");
        Assert(
            pvp.Items.Any(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.IconLink)) &&
            pve.Items.Any(item => !string.IsNullOrWhiteSpace(item.Name)),
            "Live JSON task tracker did not resolve item metadata.");
    }

    /// <summary>Opt-in CDN check; normal regression tests remain fully offline.</summary>
    private static async Task VerifyLiveItemIconLoading()
    {
        var ids = new[]
        {
            "5c110624d174af029e69734c",
            "5448be9a4bdc2dfd2f8b456a",
            "5448c12b4bdc2d02308b456f",
            "5448c1d04bdc2dff2f8b4569",
            "5448fee04bdc2dbc018b4567",
            "5448ff904bdc2d6f028b456e"
        };
        var sources = await Task.WhenAll(ids.Select(id => ItemIconService.GetAsync(
            id,
            $"https://assets.tarkov.dev/{id}-grid-image.webp",
            $"https://assets.tarkov.dev/{id}-icon.webp")));
        Assert(
            sources.All(source => source is { PixelWidth: > 0, PixelHeight: > 0 }),
            "Bounded live icon loading did not return a decodable image for every fixture.");
    }

    private static void VerifyBtrComponent()
    {
        var adapter = new BtrPredictionAdapter();
        Assert(adapter.IsAvailable, $"BTR component is unavailable: {adapter.UnavailableReason}");
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static string FindUpward(string relativePath)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException($"Unable to locate {relativePath}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
