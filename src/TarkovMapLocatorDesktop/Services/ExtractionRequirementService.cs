using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Versioned, local extraction guidance for the maps shipped by the desktop tool.
/// The source material was checked against the Official Escape from Tarkov Wiki on
/// 2026-07-21. Keep this deliberately independent of maps_detail.json: that file
/// contains coordinates and names, but not reliable extraction requirements.
/// </summary>
public static class ExtractionRequirementService
{
    private sealed record Rule(
        string Availability,
        string UseLimit,
        string Conditions,
        string? FactionOverride = null);

    private static readonly Rule StandardExtractRule = new(
        "固定开放",
        "可重复使用",
        "无额外物品或操作要求；仍须出现在本局撤离列表中。");

    private static ExtractionDataFile? _loadedData;
    private static readonly IReadOnlyDictionary<string, Rule> Rules = LoadRules();

    public static string DataVersion => _loadedData?.DataVersion ?? "unknown";
    public static int RuleCount => Rules.Count;

    private static IReadOnlyDictionary<string, Rule> LoadRules()
    {
        const string fileName = "extraction-requirements.json";
        const string resourceName = "TarkovMapLocatorDesktop.Resources.ExtractionRequirements.json";
        var externalPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(externalPath))
        {
            try
            {
                using var stream = File.OpenRead(externalPath);
                return ParseRules(stream, externalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                RuntimeLogService.Warning("撤离条件", "外部撤离条件资料无效，改用程序内置副本", $"文件: {externalPath}\n{exception}");
            }
        }

        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                             ?? throw new FileNotFoundException("程序内置撤离条件资料缺失。", resourceName);
        return ParseRules(embedded, resourceName);
    }

    private static IReadOnlyDictionary<string, Rule> ParseRules(Stream stream, string origin)
    {
        var data = JsonSerializer.Deserialize<ExtractionDataFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("撤离条件资料为空。");
        if (data.SchemaVersion != 1 || data.Rules.Length == 0)
            throw new InvalidDataException($"不支持的撤离条件资料版本：{data.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(data.DataVersion) || data.SourceCheckedAt == default)
            throw new InvalidDataException("撤离条件资料缺少版本或核对日期。");

        var result = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in data.Rules)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Availability) ||
                string.IsNullOrWhiteSpace(entry.UseLimit) || string.IsNullOrWhiteSpace(entry.Conditions))
                throw new InvalidDataException("撤离条件资料包含不完整条目。");
            if (!result.TryAdd(entry.Key, new Rule(entry.Availability, entry.UseLimit, entry.Conditions, entry.FactionOverride)))
                throw new InvalidDataException($"撤离条件资料包含重复键：{entry.Key}");
        }

        foreach (var standardKey in data.StandardExtractKeys)
        {
            if (string.IsNullOrWhiteSpace(standardKey) || standardKey.Count(character => character == '|') != 2)
                throw new InvalidDataException($"撤离条件资料包含无效标准出口键：{standardKey}");
            if (!result.TryAdd(standardKey, StandardExtractRule))
                throw new InvalidDataException($"撤离条件资料包含重复键：{standardKey}");
        }

        data.Origin = origin;
        _loadedData = data;
        return result;
    }


    public static string BuildToolTip(string mapKey, string markerType, string label, string? faction)
    {
        if (string.Equals(markerType, "transit", StringComparison.OrdinalIgnoreCase))
            return BuildTransitToolTip(mapKey, label, faction);

        var exactKey = Key(mapKey, label, faction);
        var generalKey = Key(mapKey, label);
        if (NormalizeToken(faction) is "all" or "any" or "both" && !Rules.ContainsKey(generalKey))
        {
            var hasPmc = Rules.TryGetValue(Key(mapKey, label, "pmc"), out var pmc);
            var hasScav = Rules.TryGetValue(Key(mapKey, label, "scav"), out var scav);
            if (hasPmc && hasScav)
            {
                if (pmc == scav)
                    return $"阵营：PMC / Scav\n开放：{pmc!.Availability} · {pmc.UseLimit}\n条件：{pmc.Conditions}";
                return $"PMC\n开放：{pmc!.Availability} · {pmc.UseLimit}\n条件：{pmc.Conditions}\n\n" +
                       $"Scav\n开放：{scav!.Availability} · {scav.UseLimit}\n条件：{scav.Conditions}";
            }
        }
        var rule = Rules.TryGetValue(exactKey, out var exact)
            ? exact
            : Rules.TryGetValue(generalKey, out var general)
                ? general
                : new Rule(
                    "资料未覆盖 · 以本局列表为准",
                    "以游戏内状态为准",
                    "该点位尚未收录独立规则，请以游戏内撤离提示为准。");

        var factionText = rule.FactionOverride ?? FormatFaction(faction, mapKey);
        return $"阵营：{factionText}\n开放：{rule.Availability} · {rule.UseLimit}\n条件：{rule.Conditions}";
    }

    public static bool HasExplicitRule(string mapKey, string label, string? faction)
    {
        var exactKey = Key(mapKey, label, faction);
        var generalKey = Key(mapKey, label);
        if (Rules.ContainsKey(exactKey) || Rules.ContainsKey(generalKey)) return true;
        return NormalizeToken(faction) is "all" or "any" or "both" &&
               Rules.ContainsKey(Key(mapKey, label, "pmc")) &&
               Rules.ContainsKey(Key(mapKey, label, "scav"));
    }


    private static string BuildTransitToolTip(string mapKey, string label, string? faction)
    {
        var normalizedMap = NormalizeToken(mapKey);
        var normalizedLabel = NormalizeLabel(label);
        var condition = "无额外物品要求。";

        if ((normalizedMap is "streets-of-tarkov" or "factory") && normalizedLabel.Contains("实验室", StringComparison.OrdinalIgnoreCase))
            condition = "每名玩家需要一张 TerraGroup 实验室门禁卡。";
        else if (normalizedMap == "shoreline" && normalizedLabel.Contains("迷宫", StringComparison.OrdinalIgnoreCase))
            condition = "每名玩家需要一张 Labrys 门禁卡；入口门还需要 Knossos LLC 设施钥匙。";

        return $"阵营：{FormatFaction(faction, mapKey)}\n开放：开局 1 分钟后 · 可重复使用\n条件：{condition}";
    }


    private static string FormatFaction(string? faction, string mapKey) => NormalizeToken(faction) switch
    {
        "pmc" => "PMC",
        "scav" => "Scav",
        "all" or "any" or "both" => "PMC / Scav",
        _ when NormalizeToken(mapKey) is "the-lab" or "the-labyrinth" => "PMC",
        _ => "PMC / Scav"
    };

    private static string Key(string mapKey, string label, string? faction = null) =>
        $"{NormalizeToken(mapKey)}|{NormalizeLabel(label)}|{(string.IsNullOrWhiteSpace(faction) ? "*" : NormalizeToken(faction))}";

    private static string NormalizeToken(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    private static string NormalizeLabel(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace("撤离点", "", StringComparison.Ordinal)
            .Trim()
            .Trim(' ', '·', '-', '—', ':', '：', '?', '？');
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character)));
    }

    private sealed class ExtractionDataFile
    {
        public int SchemaVersion { get; set; }
        public string DataVersion { get; set; } = "";
        public string Source { get; set; } = "";
        public DateOnly SourceCheckedAt { get; set; }
        public string[] StandardExtractKeys { get; set; } = [];
        public ExtractionDataRule[] Rules { get; set; } = [];
        public string Origin { get; set; } = "";
    }

    private sealed class ExtractionDataRule
    {
        public string Key { get; set; } = "";
        public string Availability { get; set; } = "";
        public string UseLimit { get; set; } = "";
        public string Conditions { get; set; } = "";
        public string? FactionOverride { get; set; }
    }
}
