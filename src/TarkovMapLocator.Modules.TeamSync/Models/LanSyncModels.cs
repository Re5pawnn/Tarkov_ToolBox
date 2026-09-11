namespace TarkovMapLocator.Modules.TeamSync.Models;

public sealed record LanSyncConfig(
    bool Enabled,
    string Mode,
    string DisplayName,
    string Color,
    string RemoteEndpoint,
    string RoomKey,
    int SyncPort)
{
    public const int DefaultPort = 17428;

    public static LanSyncConfig Default { get; } = new(false, "host", "本机玩家", "#4fd1ff", "", "", DefaultPort);

    public LanSyncConfig Normalize() => new(
        Enabled,
        string.Equals(Mode?.Trim(), "join", StringComparison.OrdinalIgnoreCase) ? "join" : "host",
        NormalizeDisplayName(DisplayName),
        NormalizeColor(Color),
        (RemoteEndpoint ?? "").Trim()[..Math.Min((RemoteEndpoint ?? "").Trim().Length, 128)],
        (RoomKey ?? "").Trim()[..Math.Min((RoomKey ?? "").Trim().Length, 128)],
        SyncPort is >= 1 and <= 65535 ? SyncPort : DefaultPort);

    public static string NormalizeDisplayName(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return "本机玩家";
        return text.Length <= 24 ? text : text[..24];
    }

    public static string NormalizeColor(string? value)
    {
        var text = value?.Trim() ?? "";
        return System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")
            ? text.ToLowerInvariant()
            : "#4fd1ff";
    }
}

public sealed record LanColorOption(string Value, string Label);

public sealed record LanPeerMarker(
    string PeerId,
    string DisplayName,
    string Color,
    double X,
    double Y,
    double WorldX,
    double? WorldHeight,
    double WorldZ,
    double HeadingDegrees,
    DateTimeOffset LastSeenAt);

public sealed record LanPeerListItem(
    string DisplayName,
    string Color,
    string MapName,
    string LastSeenText,
    bool IsVisibleOnCurrentMap,
    bool HasPosition);

public sealed record LanSyncUiState(
    string Mode,
    string Status,
    bool IsRunning,
    string LocalEndpoint,
    string LastError,
    IReadOnlyList<LanPeerListItem> Peers);
