using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TarkovMapLocator.App.Services;

public sealed class LocalPathConfigService
{
    private const string AppStateDirectoryName = "TarkovMapLocator";
    private const string LocalPathsFileName = "local-paths.json";
    private const string UiPreferencesFileName = "ui-preferences.json";
    private const string LanSyncFileName = "lan-sync.json";
    private const string ScreenFilterFileName = "screen-filter.json";
    private const string AppSettingsFileName = "app-settings.json";
    private const string DefaultLanDisplayName = "本机玩家";
    private const string DefaultLanColor = "#4fd1ff";
    private const int DefaultLanPort = 39247;
    private const int LanDisplayNameMaxLength = 24;
    private const int LanHostMaxLength = 128;

    private static readonly Regex HexColorRegex = new(
        "^#[0-9a-fA-F]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> TrackerSortModes = new(StringComparer.Ordinal)
    {
        "default",
        "quantity-desc",
        "quantity-asc",
        "name-asc",
        "name-desc"
    };

    private readonly string appStateDirectory;

    public LocalPathConfigService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppStateDirectoryName))
    {
    }

    public LocalPathConfigService(string appStateDirectory)
    {
        this.appStateDirectory = appStateDirectory;
    }

    public string LocalPathsPath => Path.Combine(appStateDirectory, LocalPathsFileName);

    public string UiPreferencesPath => Path.Combine(appStateDirectory, UiPreferencesFileName);

    public string LanSyncPath => Path.Combine(appStateDirectory, LanSyncFileName);

    public string ScreenFilterPath => Path.Combine(appStateDirectory, ScreenFilterFileName);

    public string AppSettingsPath => Path.Combine(appStateDirectory, AppSettingsFileName);

    public IReadOnlyDictionary<string, string> ReadLocalPaths()
    {
        var payload = ReadJsonObject(LocalPathsPath);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "screenshot", "game" })
        {
            var value = payload[key]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                paths[key] = value;
            }
        }

        return paths;
    }

    public void SaveLocalPath(string kind, string path)
    {
        if (kind is not ("screenshot" or "game"))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Unsupported local path kind.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        var payload = ReadJsonObject(LocalPathsPath);
        payload[kind] = path;
        payload["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        WriteJsonAtomic(LocalPathsPath, payload);
    }

    public JsonObject ReadUiPreferences()
    {
        return SanitizeUiPreferences(ReadJsonObject(UiPreferencesPath));
    }

    public JsonObject SaveUiPreferences(JsonObject? payload)
    {
        var preferences = SanitizeUiPreferences(payload);
        preferences["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        WriteJsonAtomic(UiPreferencesPath, preferences);
        return preferences;
    }

    public JsonObject ReadLanSyncConfig()
    {
        return SanitizeLanSyncConfig(ReadJsonObject(LanSyncPath));
    }

    public JsonObject SaveLanSyncConfig(JsonObject? payload)
    {
        var config = SanitizeLanSyncConfig(payload);
        config["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        WriteJsonAtomic(LanSyncPath, config);
        return config;
    }

    public JsonObject ReadScreenFilterConfig()
    {
        return SanitizeScreenFilterConfig(ReadJsonObject(ScreenFilterPath));
    }

    public JsonObject SaveScreenFilterConfig(JsonObject? payload)
    {
        var config = SanitizeScreenFilterConfig(payload);
        config["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        WriteJsonAtomic(ScreenFilterPath, config);
        return config;
    }

    public JsonObject ReadAppSettings()
    {
        return SanitizeAppSettings(ReadJsonObject(AppSettingsPath));
    }

    public string ReadStartupPage()
    {
        return ReadString(ReadAppSettings(), "startupPage") ?? "native-map";
    }

    public JsonObject SaveStartupPage(string startupPage)
    {
        var settings = ReadAppSettings();
        settings["startupPage"] = startupPage;
        settings = SanitizeAppSettings(settings);
        settings["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        WriteJsonAtomic(AppSettingsPath, settings);
        return settings;
    }

    public JsonObject SaveInstallLocation(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("Install directory cannot be empty.", nameof(installDirectory));
        }

        var settings = ReadAppSettings();
        settings["installDirectory"] = Path.GetFullPath(installDirectory.Trim());
        settings = SanitizeAppSettings(settings);
        settings["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        WriteJsonAtomic(AppSettingsPath, settings);
        return settings;
    }

    private static JsonObject SanitizeUiPreferences(JsonObject? payload)
    {
        payload ??= [];

        var preferences = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["marketMode"] = NormalizePriceMode(ReadString(payload, "marketMode")),
            ["trackerMode"] = NormalizePriceMode(ReadString(payload, "trackerMode")),
            ["trackerIncludeTasks"] = ReadBoolean(payload, "trackerIncludeTasks", fallback: true),
            ["trackerIncludeHideout"] = ReadBoolean(payload, "trackerIncludeHideout", fallback: true),
            ["trackerSortMode"] = NormalizeTrackerSortMode(ReadString(payload, "trackerSortMode")),
            ["nativeMapId"] = NormalizePreferenceText(ReadString(payload, "nativeMapId"), 96),
            ["nativeMapBase"] = NormalizeMapBase(ReadString(payload, "nativeMapBase")),
            ["nativeMapLayer"] = NormalizeMapLayer(ReadString(payload, "nativeMapLayer")),
            ["nativePoiSource"] = NormalizePoiSource(ReadString(payload, "nativePoiSource")),
            ["nativeShowPoi"] = ReadBoolean(payload, "nativeShowPoi", fallback: true),
            ["nativeAutoRefresh"] = ReadBoolean(payload, "nativeAutoRefresh", fallback: false),
            ["nativeAutoMap"] = ReadBoolean(payload, "nativeAutoMap", fallback: false),
            ["nativeRaidDetailsExpanded"] = ReadBoolean(payload, "nativeRaidDetailsExpanded", fallback: false),
            ["nativeMapPipEnabled"] = ReadBoolean(payload, "nativeMapPipEnabled", fallback: false),
            ["nativeMapPipOpacity"] = NormalizeDouble(ReadNumber(payload, "nativeMapPipOpacity"), 0.35, 1.0, 0.92),
            ["nativeMapPipZoom"] = NormalizeDouble(ReadNumber(payload, "nativeMapPipZoom"), 1.0, 5.0, 1.0),
            ["nativeMapPipLeft"] = NormalizeInt(ReadInt(payload, "nativeMapPipLeft"), 0, 20000, 80),
            ["nativeMapPipTop"] = NormalizeInt(ReadInt(payload, "nativeMapPipTop"), 0, 20000, 80),
            ["nativeMapPipWidth"] = NormalizeInt(ReadInt(payload, "nativeMapPipWidth"), 220, 1200, 420),
            ["nativeMapPipHeight"] = NormalizeInt(ReadInt(payload, "nativeMapPipHeight"), 220, 1200, 320),
            ["nativePoiFilters"] = SanitizePoiFilters(payload["nativePoiFilters"] as JsonObject)
        };

        var updatedAt = ReadNumber(payload, "updatedAt");
        preferences["updatedAt"] = updatedAt is null ? null : JsonValue.Create(updatedAt.Value);
        return preferences;
    }

    private static string NormalizePriceMode(string? value)
    {
        return string.Equals(value?.Trim(), "pve", StringComparison.OrdinalIgnoreCase) ? "pve" : "pvp";
    }

    private static string NormalizeTrackerSortMode(string? value)
    {
        var mode = value?.Trim() ?? "";
        return TrackerSortModes.Contains(mode) ? mode : "default";
    }

    private static string NormalizeMapBase(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant();
        return mode is "svg" ? "svg" : "cache";
    }

    private static string NormalizePoiSource(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant();
        return mode is "labels" or "more" ? mode : "extracts";
    }

    private static string NormalizeMapLayer(string? value)
    {
        var text = NormalizePreferenceText(value, 64, "__SURFACE__");
        return string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "__AUTO_LAYER__", StringComparison.OrdinalIgnoreCase)
                ? "__SURFACE__"
                : text;
    }

    private static string NormalizePreferenceText(string? value, int maxLength, string fallback = "")
    {
        var text = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static JsonObject SanitizePoiFilters(JsonObject? payload)
    {
        var result = new JsonObject();
        if (payload is null)
        {
            return result;
        }

        foreach (var item in payload)
        {
            var key = NormalizePreferenceText(item.Key, 64);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            try
            {
                result[key] = item.Value?.GetValue<bool>() ?? true;
            }
            catch
            {
                result[key] = true;
            }
        }

        return result;
    }

    private static JsonObject SanitizeLanSyncConfig(JsonObject? payload)
    {
        payload ??= [];

        var requestedMode = NormalizeLanMode(ReadString(payload, "mode"));
        var syncMode = NormalizeSyncMode(ReadString(payload, "syncMode") ?? (requestedMode == "join" ? "join" : "host"));
        var enabled = ReadBoolean(payload, "enabled", fallback: requestedMode is "host" or "join");
        var mode = enabled ? syncMode : "off";
        if (!enabled && !HasExplicitLanMode(payload))
        {
            mode = "off";
        }

        if (enabled && mode == "off")
        {
            mode = "host";
        }

        var remoteEndpoint = ReadString(payload, "remoteEndpoint");
        var remoteHost = ReadString(payload, "remoteHost");
        var remotePort = ReadInt(payload, "remotePort");
        if ((!string.IsNullOrWhiteSpace(remoteEndpoint) && string.IsNullOrWhiteSpace(remoteHost)) || remotePort is null)
        {
            var parsedEndpoint = ParseRemoteEndpoint(remoteEndpoint);
            remoteHost = string.IsNullOrWhiteSpace(remoteHost) ? parsedEndpoint.Host : remoteHost;
            remotePort ??= parsedEndpoint.Port;
        }

        var normalizedRemoteHost = syncMode == "join" ? NormalizeHost(remoteHost) : "";
        var normalizedRemotePort = syncMode == "join" ? NormalizePort(remotePort, DefaultLanPort) : DefaultLanPort;
        var config = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["enabled"] = enabled && mode is "host" or "join",
            ["mode"] = mode,
            ["syncMode"] = syncMode,
            ["displayName"] = NormalizeDisplayName(ReadString(payload, "displayName")),
            ["color"] = NormalizeColor(ReadString(payload, "color")),
            ["remoteHost"] = normalizedRemoteHost,
            ["remotePort"] = normalizedRemotePort,
            ["remoteEndpoint"] = syncMode == "join" ? FormatRemoteEndpoint(normalizedRemoteHost, normalizedRemotePort) : ""
        };

        var updatedAt = ReadNumber(payload, "updatedAt");
        config["updatedAt"] = updatedAt is null ? null : JsonValue.Create(updatedAt.Value);
        return config;
    }

    private static JsonObject SanitizeScreenFilterConfig(JsonObject? payload)
    {
        payload ??= [];

        var config = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["presetId"] = NormalizeScreenFilterPresetId(ReadString(payload, "presetId")),
            ["displayName"] = NormalizePreferenceText(ReadString(payload, "displayName"), 64),
            ["gamma"] = NormalizeDouble(ReadNumber(payload, "gamma"), 0.2, 5.0, 1.0),
            ["brightness"] = NormalizeInt(ReadInt(payload, "brightness"), -100, 100, 0),
            ["contrast"] = NormalizeInt(ReadInt(payload, "contrast"), -100, 100, 0),
            ["red"] = NormalizeInt(ReadInt(payload, "red"), 0, 255, 128),
            ["green"] = NormalizeInt(ReadInt(payload, "green"), 0, 255, 128),
            ["blue"] = NormalizeInt(ReadInt(payload, "blue"), 0, 255, 128)
        };

        var updatedAt = ReadNumber(payload, "updatedAt");
        config["updatedAt"] = updatedAt is null ? null : JsonValue.Create(updatedAt.Value);
        return config;
    }

    private static JsonObject SanitizeAppSettings(JsonObject? payload)
    {
        payload ??= [];
        var settings = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["startupPage"] = NormalizeStartupPage(ReadString(payload, "startupPage")),
            ["installDirectory"] = NormalizeInstallDirectory(ReadString(payload, "installDirectory"))
        };

        var updatedAt = ReadNumber(payload, "updatedAt");
        settings["updatedAt"] = updatedAt is null ? null : JsonValue.Create(updatedAt.Value);
        return settings;
    }

    private static string NormalizeStartupPage(string? value)
    {
        return "native-map";
    }

    private static string NormalizeInstallDirectory(string? value)
    {
        var text = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(text);
        }
        catch
        {
            return "";
        }
    }

    private static bool HasExplicitLanMode(JsonObject payload)
    {
        return payload.ContainsKey("mode") || payload.ContainsKey("syncMode");
    }

    private static string NormalizeLanMode(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant();
        return mode is "off" or "host" or "join" ? mode : "host";
    }

    private static string NormalizeSyncMode(string? value)
    {
        return string.Equals(value?.Trim(), "join", StringComparison.OrdinalIgnoreCase) ? "join" : "host";
    }

    private static string NormalizeDisplayName(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = DefaultLanDisplayName;
        }

        return text.Length <= LanDisplayNameMaxLength ? text : text[..LanDisplayNameMaxLength];
    }

    private static string NormalizeScreenFilterPresetId(string? value)
    {
        var presetId = value?.Trim().ToLowerInvariant();
        return presetId is "default" or "day-clear" or "day-cloudy" or "night" or "custom"
            ? presetId
            : "default";
    }

    private static string NormalizeColor(string? value)
    {
        var text = value?.Trim() ?? "";
        return HexColorRegex.IsMatch(text) ? text.ToLowerInvariant() : DefaultLanColor;
    }

    private static string NormalizeHost(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.Length <= LanHostMaxLength ? text : text[..LanHostMaxLength];
    }

    private static int NormalizePort(int? value, int fallback)
    {
        return value is >= 1 and <= 65535 ? value.Value : fallback;
    }

    private static int NormalizeInt(int? value, int min, int max, int fallback)
    {
        return value is not null ? Math.Clamp(value.Value, min, max) : fallback;
    }

    private static double NormalizeDouble(double? value, double min, double max, double fallback)
    {
        return value is not null && double.IsFinite(value.Value)
            ? Math.Clamp(value.Value, min, max)
            : fallback;
    }

    private static (string Host, int? Port) ParseRemoteEndpoint(string? value)
    {
        var text = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return ("", null);
        }

        var schemeIndex = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            text = text[(schemeIndex + 3)..].Trim();
        }

        text = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var separatorIndex = text.IndexOfAny(['/', '?', '#']);
        if (separatorIndex >= 0)
        {
            text = text[..separatorIndex].Trim();
        }

        var atIndex = text.LastIndexOf('@');
        if (atIndex >= 0)
        {
            text = text[(atIndex + 1)..].Trim();
        }

        var bracketMatch = Regex.Match(text, @"^\[([^\]]+)\]:(.+)$", RegexOptions.CultureInvariant);
        if (bracketMatch.Success)
        {
            return (NormalizeHost(bracketMatch.Groups[1].Value), ReadPortText(bracketMatch.Groups[2].Value));
        }

        var colonIndex = text.LastIndexOf(':');
        if (colonIndex < 0)
        {
            return (NormalizeHost(text), null);
        }

        return (NormalizeHost(text[..colonIndex]), ReadPortText(text[(colonIndex + 1)..]));
    }

    private static string FormatRemoteEndpoint(string host, int port)
    {
        return string.IsNullOrWhiteSpace(host) ? "" : $"{host}:{port}";
    }

    private static string? ReadString(JsonObject payload, string propertyName)
    {
        try
        {
            return payload[propertyName]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBoolean(JsonObject payload, string propertyName, bool fallback)
    {
        try
        {
            return payload[propertyName]?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static double? ReadNumber(JsonObject payload, string propertyName)
    {
        try
        {
            return payload[propertyName]?.GetValue<double>();
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject payload, string propertyName)
    {
        try
        {
            return payload[propertyName]?.GetValue<int>();
        }
        catch
        {
            return ReadPortText(ReadString(payload, propertyName));
        }
    }

    private static int? ReadPortText(string? value)
    {
        var text = value?.Trim() ?? "";
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : null;
    }

    private static JsonObject ReadJsonObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteJsonAtomic(string path, JsonObject payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(
            tempPath,
            payload.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tempPath, path, overwrite: true);
    }
}
