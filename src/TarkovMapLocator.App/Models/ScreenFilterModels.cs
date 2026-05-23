namespace TarkovMapLocator.App.Models;

public sealed record ScreenFilterPreset(
    double Gamma,
    int Brightness,
    int Contrast,
    int Red,
    int Green,
    int Blue)
{
    public static ScreenFilterPreset Default { get; } = new(1.0, 0, 0, 128, 128, 128);
}

public sealed record ScreenFilterRequest(
    string PresetId,
    string DisplayName,
    double Gamma,
    int Brightness,
    int Contrast,
    int Red,
    int Green,
    int Blue)
{
    public ScreenFilterPreset ToPreset() => new(Gamma, Brightness, Contrast, Red, Green, Blue);
}

public sealed record ScreenFilterPresetOption(
    string Id,
    string Label,
    ScreenFilterPreset Preset);

public sealed record ScreenDisplayInfo(
    string DeviceName,
    string Label,
    bool IsPrimary);

public sealed record ScreenGammaResult(
    int AppliedCount,
    int FailedCount,
    string Message)
{
    public bool IsSuccess => AppliedCount > 0 && FailedCount == 0;
}
