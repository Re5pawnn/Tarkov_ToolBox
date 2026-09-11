using System.IO;
using System.Text.Json;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Applies whichever optional feature seeds are present in the package once
/// for a local user profile. Each seed has its own marker so adding a component
/// later does not overwrite caches that another component has already refreshed.
/// </summary>
public static class PackagedBootstrapDataService
{
    private static readonly string ApplicationDataDirectory = ApplicationIdentity.ApplicationDataDirectory;
    private static readonly string TrackerApplicationDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocator");

    private static readonly string BootstrapDirectory = Path.Combine(AppContext.BaseDirectory, "bootstrap-data");
    private static readonly string SeedIdPath = Path.Combine(BootstrapDirectory, "seed-id.txt");

    private static readonly SeedDefinition[] SeedDefinitions =
    [
        new(
            "market",
            "market-cache.json",
            ApplicationDataDirectory,
            "市场行情缓存",
            path => ValidateSeedCache(path, "marketItemsPvp", "marketItemsPve", "marketItemsPvpSeason")),
        new(
            "hideout-profit",
            "hideout-profit-cache.json",
            ApplicationDataDirectory,
            "藏身处利润缓存",
            ValidateHideoutProfitSeed),
        new(
            "item-tracker",
            "item-tracker-cache.json",
            TrackerApplicationDataDirectory,
            "任务物品清单缓存",
            ValidateTrackerSeed)
    ];

    public static void ApplyFirstStartSeed()
    {
        if (!Directory.Exists(BootstrapDirectory)) return;

        try
        {
            var seedId = File.Exists(SeedIdPath) ? File.ReadAllText(SeedIdPath).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(seedId)) return;
            ValidatePackageSeed();
            var imported = ApplyAvailableSeeds(seedId);
            if (imported.Count > 0)
                RuntimeLogService.Info("初始化数据", "已导入可选组件的首次启动数据", string.Join("、", imported));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            RuntimeLogService.Warning("初始化数据", "导入本发布包首次启动数据失败，保留当前本机数据", exception.Message);
        }
    }

    internal static void ValidatePackageSeed()
    {
        if (!Directory.Exists(BootstrapDirectory))
            throw new DirectoryNotFoundException("发布包缺少首次启动种子目录。");
        if (!File.Exists(SeedIdPath) || string.IsNullOrWhiteSpace(File.ReadAllText(SeedIdPath)))
            throw new FileNotFoundException("发布包缺少首次启动种子标识。", SeedIdPath);

        var available = SeedDefinitions
            .Where(seed => File.Exists(Path.Combine(BootstrapDirectory, seed.FileName)))
            .ToArray();
        if (available.Length == 0)
            throw new FileNotFoundException("发布包没有包含任何可选组件种子数据。");

        foreach (var seed in available)
            seed.Validate(Path.Combine(BootstrapDirectory, seed.FileName));
    }

    private static IReadOnlyList<string> ApplyAvailableSeeds(string seedId)
    {
        var imported = new List<string>();
        Directory.CreateDirectory(ApplicationDataDirectory);
        foreach (var seed in SeedDefinitions)
        {
            var sourcePath = Path.Combine(BootstrapDirectory, seed.FileName);
            if (!File.Exists(sourcePath)) continue;

            var markerPath = Path.Combine(ApplicationDataDirectory, $"bootstrap-seed-{seed.Id}.txt");
            if (File.Exists(markerPath)) continue;

            Directory.CreateDirectory(seed.DestinationDirectory);
            var destinationPath = Path.Combine(seed.DestinationDirectory, seed.FileName);
            if (!File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
                imported.Add(seed.DisplayName);
            }

            var markerTemporaryPath = markerPath + ".tmp";
            File.WriteAllText(markerTemporaryPath, seedId);
            File.Move(markerTemporaryPath, markerPath, overwrite: true);
        }

        return imported;
    }

    private static void ValidateSeedCache(string path, params string[] arrayNames)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            arrayNames.Any(name =>
                !document.RootElement.TryGetProperty(name, out var values) ||
                values.ValueKind != JsonValueKind.Array ||
                values.GetArrayLength() == 0))
            throw new JsonException("市场行情种子数据缺少 PVP、PVE 或赛季服物品。");
    }

    private static void ValidateTrackerSeed(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("requirements", out var requirements) ||
            requirements.ValueKind != JsonValueKind.Object ||
            !requirements.TryGetProperty("pvp", out var pvp) || pvp.ValueKind != JsonValueKind.Array || pvp.GetArrayLength() == 0 ||
            !requirements.TryGetProperty("pve", out var pve) || pve.ValueKind != JsonValueKind.Array || pve.GetArrayLength() == 0)
            throw new JsonException("任务物品清单种子数据缺少 PVP 或 PVE 需求。");
    }

    private static void ValidateHideoutProfitSeed(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("crafts", out var crafts) || crafts.ValueKind != JsonValueKind.Object ||
            !crafts.TryGetProperty("pvp", out var pvp) || pvp.ValueKind != JsonValueKind.Array || pvp.GetArrayLength() == 0 ||
            !crafts.TryGetProperty("pve", out var pve) || pve.ValueKind != JsonValueKind.Array || pve.GetArrayLength() == 0 ||
            !crafts.TryGetProperty("pvp-season", out var season) || season.ValueKind != JsonValueKind.Array || season.GetArrayLength() == 0)
            throw new JsonException("藏身处利润种子数据缺少 PVP、PVE 或赛季服配方。");
    }

    private sealed record SeedDefinition(
        string Id,
        string FileName,
        string DestinationDirectory,
        string DisplayName,
        Action<string> Validate);
}
