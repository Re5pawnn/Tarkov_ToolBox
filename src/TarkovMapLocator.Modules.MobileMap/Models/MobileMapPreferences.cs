namespace TarkovMapLocator.Modules.MobileMap.Models;

public sealed record MobileMapPreferences(int Port, string? SakuraFrpEndpoint)
{
    public const int DefaultPort = 17428;

    public static MobileMapPreferences Default { get; } = new(DefaultPort, string.Empty);

    public MobileMapPreferences Normalize() => new(
        Port is >= 1024 and <= 65535 ? Port : DefaultPort,
        SakuraFrpEndpoint?.Trim() ?? string.Empty);
}
