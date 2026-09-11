namespace TarkovMapLocatorDesktop.Models;

public enum TaskTrackingStatus
{
    NotStarted = 0,
    Accepted = 1,
    Completed = 2
}

public sealed record DetectedTaskStatusChange(
    string TrackingTaskId,
    TaskTrackingStatus Status,
    string GameTaskId,
    DateTimeOffset ObservedAt,
    string SourceFileName);
