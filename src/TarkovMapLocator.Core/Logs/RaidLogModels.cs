namespace TarkovMapLocator.Core.Logs;

public enum RaidLogStatus
{
    NoDirectory,
    Waiting,
    Ready,
    Error
}

public enum RaidState
{
    Unknown,
    Waiting,
    Matching,
    Loading,
    InRaid,
    Ended,
    Aborted
}

public sealed record RaidLogEntry(
    DateTimeOffset Timestamp,
    string Message,
    string Json);

public sealed record RaidLogEvent(
    string Type,
    DateTimeOffset Timestamp,
    string Title,
    string Detail,
    string? MapId = null,
    string? MapName = null,
    string? Location = null,
    string? RaidId = null,
    string? SessionMode = null,
    string? ServerIp = null,
    string? ServerPort = null,
    double? QueueDurationSeconds = null,
    double? LoadDurationSeconds = null);

public sealed record RaidLogSnapshot(
    RaidLogStatus LogStatus,
    RaidState RaidState,
    string? LogDirectory,
    string? LatestLogFile,
    DateTimeOffset? LatestLogModifiedAt,
    DateTimeOffset? LastScanAt,
    string? MapId,
    string? MapName,
    string? Location,
    string? RaidId,
    string? SessionMode,
    string? ServerIp,
    string? ServerPort,
    double? QueueDurationSeconds,
    double? LoadDurationSeconds,
    DateTimeOffset? GameStartAt,
    DateTimeOffset? RaidEndAt,
    string? LastEventTitle,
    string? LastEventDetail,
    string? ErrorMessage,
    IReadOnlyList<RaidLogEvent> RecentEvents)
{
    public static RaidLogSnapshot NoDirectory() => new(
        RaidLogStatus.NoDirectory,
        RaidState.Unknown,
        null,
        null,
        null,
        DateTimeOffset.Now,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        []);
}
