using System.Text.Json.Serialization;

namespace TarkovMapLocator.Modules.ScreenFilter.Models;

[Flags]
public enum GlobalHotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public enum ScreenFilterMode
{
    GammaPanel = 0,
    AutoShade = 1
}

public sealed record GlobalHotkeyGesture(int VirtualKey, GlobalHotkeyModifiers Modifiers)
{
    private const GlobalHotkeyModifiers AllowedModifiers =
        GlobalHotkeyModifiers.Alt |
        GlobalHotkeyModifiers.Control |
        GlobalHotkeyModifiers.Shift |
        GlobalHotkeyModifiers.Windows;

    public static GlobalHotkeyGesture None { get; } = new(0, GlobalHotkeyModifiers.None);

    public bool IsConfigured => Normalize().VirtualKey != 0;

    public GlobalHotkeyGesture Normalize()
    {
        var virtualKey = IsAllowedMainKey(VirtualKey) ? VirtualKey : 0;
        return virtualKey == 0
            ? None
            : new GlobalHotkeyGesture(virtualKey, Modifiers & AllowedModifiers);
    }

    private static bool IsAllowedMainKey(int virtualKey) =>
        (virtualKey is 0x05 or 0x06 || virtualKey is >= 0x08 and <= 0xFE) && virtualKey is not
            0x10 and not 0x11 and not 0x12 and
            not 0x5B and not 0x5C and
            not 0xA0 and not 0xA1 and not 0xA2 and not 0xA3 and not 0xA4 and not 0xA5;
}

/// <summary>
/// One Gamma Panel colour channel. Gamma and contrast use the integer values
/// shown by Gamma Panel (100 = 1.00); brightness is the signed UI value.
/// </summary>
public sealed record ScreenFilterChannel(int Gamma, int Brightness, int Contrast)
{
    public static ScreenFilterChannel Default { get; } = new(100, 0, 100);

    public ScreenFilterChannel Normalize() => new(
        Math.Clamp(Gamma, 10, 400),
        Math.Clamp(Brightness, -100, 100),
        Math.Clamp(Contrast, 0, 200));
}

/// <summary>
/// Gamma Panel-compatible RGB settings. Each channel is intentionally
/// independent; LinkChannels is only a UI editing aid.
/// </summary>
public sealed record ScreenFilterPreset(
    ScreenFilterChannel Red,
    ScreenFilterChannel Green,
    ScreenFilterChannel Blue)
{
    public static ScreenFilterPreset Default { get; } = Uniform(100, 0, 100);

    public static ScreenFilterPreset Uniform(int gamma, int brightness, int contrast)
    {
        var channel = new ScreenFilterChannel(gamma, brightness, contrast).Normalize();
        return new ScreenFilterPreset(channel, channel, channel);
    }

    public ScreenFilterPreset Normalize() => new(
        (Red ?? ScreenFilterChannel.Default).Normalize(),
        (Green ?? ScreenFilterChannel.Default).Normalize(),
        (Blue ?? ScreenFilterChannel.Default).Normalize());
}

public sealed record ScreenFilterRequest
{
    public const int CurrentSchemaVersion = 2;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string PresetId { get; init; } = "default";
    public string DisplayName { get; init; } = "";
    public ScreenFilterMode Mode { get; init; } = ScreenFilterMode.GammaPanel;
    public bool LinkChannels { get; init; } = true;
    public GlobalHotkeyGesture ToggleHotkey { get; init; } = GlobalHotkeyGesture.None;
    public ScreenFilterChannel Red { get; init; } = ScreenFilterChannel.Default;
    public ScreenFilterChannel Green { get; init; } = ScreenFilterChannel.Default;
    public ScreenFilterChannel Blue { get; init; } = ScreenFilterChannel.Default;

    public static ScreenFilterRequest Default { get; } = new();

    public ScreenFilterPreset ToPreset() => new ScreenFilterPreset(Red, Green, Blue).Normalize();

    public ScreenFilterRequest Normalize()
    {
        // Older files described a different six-parameter algorithm. They are
        // deliberately discarded instead of pretending those values are
        // Gamma Panel parameters.
        if (SchemaVersion != CurrentSchemaVersion) return Default;

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            PresetId = NormalizePresetId(PresetId),
            DisplayName = DisplayName?.Trim() ?? "",
            Mode = Enum.IsDefined(Mode) ? Mode : ScreenFilterMode.GammaPanel,
            ToggleHotkey = (ToggleHotkey ?? GlobalHotkeyGesture.None).Normalize(),
            Red = (Red ?? ScreenFilterChannel.Default).Normalize(),
            Green = (Green ?? ScreenFilterChannel.Default).Normalize(),
            Blue = (Blue ?? ScreenFilterChannel.Default).Normalize()
        };
    }

    private static string NormalizePresetId(string? presetId) => presetId?.Trim().ToLowerInvariant() switch
    {
        "default" or "day-clear" or "day-cloudy" or "night" or "custom" => presetId.Trim().ToLowerInvariant(),
        _ => "default"
    };
}

public sealed record ScreenFilterPresetOption(string Id, string Label, ScreenFilterPreset Preset);

public sealed record ScreenFilterSavedPreset(
    string Id,
    string Name,
    bool LinkChannels,
    ScreenFilterChannel Red,
    ScreenFilterChannel Green,
    ScreenFilterChannel Blue)
{
    public ScreenFilterSavedPreset Normalize()
    {
        var id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        var name = string.IsNullOrWhiteSpace(Name) ? "未命名配置" : Name.Trim();
        if (name.Length > 40) name = name[..40];
        var red = (Red ?? ScreenFilterChannel.Default).Normalize();
        var green = LinkChannels ? red : (Green ?? ScreenFilterChannel.Default).Normalize();
        var blue = LinkChannels ? red : (Blue ?? ScreenFilterChannel.Default).Normalize();
        return new ScreenFilterSavedPreset(id, name, LinkChannels, red, green, blue);
    }

    public ScreenFilterPreset ToPreset() => new ScreenFilterPreset(Red, Green, Blue).Normalize();
}

public sealed record ScreenDisplayInfo(string DeviceName, string Label, bool IsPrimary);

public sealed record ScreenGammaResult(int AppliedCount, int FailedCount, string Message)
{
    public bool IsSuccess => AppliedCount > 0 && FailedCount == 0;
}
