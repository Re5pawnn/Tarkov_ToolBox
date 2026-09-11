namespace TarkovMapLocatorDesktop.Models;

public sealed record LiveCoordinate(
    string FileName,
    double X,
    double Y,
    double Z,
    double YawDegrees,
    DateTimeOffset CapturedAt,
    long SortOrder,
    DateTimeOffset? FileNameTimestamp = null);

public sealed record LocalRaidSnapshot(
    string? MapKey,
    string? MapSource,
    string? MapDetectionDetail,
    LiveCoordinate? Coordinate,
    string StatusMessage,
    bool HasConfiguredPaths,
    string? ScreenshotDirectory,
    string? GameLogDirectory,
    string? LatestLogFileName,
    DateTimeOffset? GameStartAt,
    DateTimeOffset? RaidEndAt,
    // Timestamp of the most recent log record that positively identified the
    // current map.  A transfer stays in the same raid, so GameStarted alone is
    // not enough to tell an old screenshot from a new-map screenshot.
    DateTimeOffset? MapObservedAt = null,
    // The monitor can take several bounded reads to reach the end of a large
    // application log.  Until then the newest token seen is historical data,
    // not reliable evidence for an automatic map switch.
    bool IsMapLogCaughtUp = true,
    // Some legacy transfer records only name the source map.  They prove that
    // a transfer is in progress, but cannot safely identify its destination.
    bool IsTransitDestinationPending = false);
