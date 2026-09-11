using System.IO;
using System.Text.Json;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Stores desktop-only choices separately from the original application's files.
/// </summary>
public static class DesktopPreferencesService
{
    private static readonly string PreferencePath = Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "preferences.json");

    public static DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return DesktopPreferences.Empty;
            var preferences = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(PreferencePath));
            return preferences?.Normalize() ?? DesktopPreferences.Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("配置", "读取桌面配置失败，已使用默认值", exception.ToString());
            return DesktopPreferences.Empty;
        }
    }

    public static bool SaveTaskState(IEnumerable<string> selectedTaskKeys, IEnumerable<string> completedTaskKeys)
    {
        var current = Load();
        return Save(current with
        {
            SelectedTaskKeys = selectedTaskKeys.ToArray(),
            CompletedTaskKeys = completedTaskKeys.ToArray()
        });
    }

    public static bool SavePaths(string? screenshotDirectory, string? gameLogDirectory)
    {
        var current = Load();
        return Save(current with
        {
            ScreenshotDirectory = screenshotDirectory,
            GameLogDirectory = gameLogDirectory
        });
    }

    public static bool SaveMapLayerState(bool autoSwitchMapLayer, IReadOnlyDictionary<string, string> selectedMapLayers)
    {
        var current = Load();
        return Save(current with
        {
            AutoSwitchMapLayer = autoSwitchMapLayer,
            SelectedMapLayers = new Dictionary<string, string>(selectedMapLayers, StringComparer.OrdinalIgnoreCase)
        });
    }

    public static bool SaveMapStyleState(IReadOnlyDictionary<string, string> selectedMapStyles)
    {
        var current = Load();
        return Save(current with
        {
            SelectedMapStyles = new Dictionary<string, string>(selectedMapStyles, StringComparer.OrdinalIgnoreCase)
        });
    }

    public static bool SaveTaskRecognitionState(bool enabled)
    {
        var current = Load();
        return Save(current with { AutoRecognizeTaskStatuses = enabled });
    }

    public static bool SaveTaskPrerequisiteCompletionState(bool enabled)
    {
        var current = Load();
        return Save(current with { AutoCompleteTaskPrerequisites = enabled });
    }

    /// <summary>
    /// Remembers the explicit action chosen from the main window close prompt.
    /// Keeping this with the other desktop-only preferences means an update does
    /// not reset a user's preferred close-to-taskbar behavior.
    /// </summary>
    public static bool SaveCloseBehavior(bool suppressClosePrompt, bool minimizeWhenClosing)
    {
        var current = Load();
        return Save(current with
        {
            SuppressClosePrompt = suppressClosePrompt,
            MinimizeWhenClosing = minimizeWhenClosing
        });
    }

    public static bool SaveMapWorkspaceLayout(double inspectorPaneWidth, double? inspectorPaneHeight)
    {
        var current = Load();
        return Save(current with
        {
            MapInspectorPaneWidth = inspectorPaneWidth,
            MapInspectorPaneHeight = inspectorPaneHeight
        });
    }

    private static bool Save(DesktopPreferences preferences)
    {
        try
        {
            var directory = Path.GetDirectoryName(PreferencePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = PreferencePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, PreferencePath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience. A read-only or unavailable profile must not affect the map.
            RuntimeLogService.Warning("配置", "保存桌面配置失败，本次更改仅在当前会话生效", exception.ToString());
            return false;
        }
    }

}

public sealed record DesktopPreferences(
    string[] SelectedTaskKeys,
    string[] CompletedTaskKeys,
    string? ScreenshotDirectory = null,
    string? GameLogDirectory = null,
    bool AutoSwitchMapLayer = true,
    Dictionary<string, string>? SelectedMapLayers = null,
    bool SuppressClosePrompt = false,
    bool MinimizeWhenClosing = false,
    bool AutoRecognizeTaskStatuses = true,
    bool AutoCompleteTaskPrerequisites = true,
    Dictionary<string, string>? SelectedMapStyles = null,
    double? MapInspectorPaneWidth = null,
    double? MapInspectorPaneHeight = null)
{
    public static DesktopPreferences Empty { get; } = new([], [], AutoSwitchMapLayer: true, SelectedMapLayers: new(StringComparer.OrdinalIgnoreCase));

    public DesktopPreferences Normalize() => new(
        (SelectedTaskKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
        (CompletedTaskKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
        string.IsNullOrWhiteSpace(ScreenshotDirectory) ? null : ScreenshotDirectory.Trim(),
        string.IsNullOrWhiteSpace(GameLogDirectory) ? null : GameLogDirectory.Trim(),
        AutoSwitchMapLayer,
        NormalizeMapLayers(SelectedMapLayers),
        SuppressClosePrompt,
        MinimizeWhenClosing,
        AutoRecognizeTaskStatuses,
        AutoCompleteTaskPrerequisites,
        NormalizeMapStyles(SelectedMapStyles),
        NormalizePaneWidth(MapInspectorPaneWidth, 260),
        NormalizePaneHeight(MapInspectorPaneHeight, 360));

    private static Dictionary<string, string> NormalizeMapLayers(IEnumerable<KeyValuePair<string, string>>? layers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in layers ?? [])
        {
            var mapId = pair.Key?.Trim();
            var layerId = pair.Value?.Trim();
            if (string.IsNullOrWhiteSpace(mapId) || string.IsNullOrWhiteSpace(layerId)) continue;
            result[mapId] = layerId;
        }
        return result;
    }

    private static Dictionary<string, string> NormalizeMapStyles(IEnumerable<KeyValuePair<string, string>>? styles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in styles ?? [])
        {
            var mapId = pair.Key?.Trim();
            var style = pair.Value?.Trim().ToLowerInvariant();
            if (style is "helper-test" or "web-map" or "satellite") style = "satellite-map";
            if (string.IsNullOrWhiteSpace(mapId) || style is not ("2d" or "satellite-map")) continue;
            result[mapId] = style;
        }
        return result;
    }

    private static double? NormalizePaneWidth(double? value, double minimum) =>
        value is { } width && double.IsFinite(width) && width >= minimum ? Math.Clamp(width, minimum, 5000) : null;

    private static double? NormalizePaneHeight(double? value, double minimum) =>
        value is { } height && double.IsFinite(height) && height >= minimum ? Math.Clamp(height, minimum, 5000) : null;

}
