namespace TarkovMapLocator.Modules.TaskTracking.Models;

public enum TaskTrackingStatus
{
    NotStarted = 0,
    Accepted = 1,
    Completed = 2
}

public sealed class TaskTrackingCatalog
{
    public int SchemaVersion { get; init; }
    public string SourceUrl { get; init; } = "";
    public string SourceApiUrl { get; init; } = "";
    public IReadOnlyList<string> SourceApiUrls { get; init; } = [];
    public string? SourceLastModified { get; init; }
    public DateTimeOffset? GeneratedAtUtc { get; init; }
    public IReadOnlyList<string> DataVersions { get; init; } = [];
    public IReadOnlyList<TaskTrackingTask> Tasks { get; init; } = [];
    public IReadOnlyList<TaskTrackingLine> Lines { get; init; } = [];
    public IReadOnlyList<string> SupplementSources { get; init; } = [];
}

public sealed class TaskTrackingLine
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
}

public sealed class TaskTrackingTask
{
    public string Id { get; init; } = "";
    public string Mode { get; init; } = "pve";
    public string SourceTaskId { get; init; } = "";
    public string SourceDefinitionId { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> GameTaskIds { get; init; } = [];
    public string SectionKey { get; init; } = "";
    public string TraderKey { get; init; } = "";
    public string TraderName { get; init; } = "";
    public string GroupKey { get; init; } = "";
    public string GroupName { get; init; } = "";
    public int GroupOrder { get; init; }
    public int RequiredTraderLevel { get; init; }
    public int Sequence { get; init; }
    public int Level { get; init; }
    public string MapName { get; init; } = "";
    public IReadOnlyList<string> Objectives { get; init; } = [];
    public string WikiUrl { get; init; } = "";
    public IReadOnlyList<string> PreviousTaskIds { get; init; } = [];
    public IReadOnlyList<string> NextTaskIds { get; init; } = [];
    public bool Available { get; init; } = true;
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 350;
    public double Height { get; init; } = 230;
    public bool IsSupplemental { get; init; }
}

public sealed record TaskTrackingCatalogLoadResult(TaskTrackingCatalog? Catalog, string? ErrorMessage)
{
    public bool IsAvailable => Catalog is { Tasks.Count: > 0 };
}

public sealed record TaskTrackingTraderOption(string Key, string Name, int Count)
{
    public string Label => Count > 0 ? $"{Name}  {Count}" : Name;
}

public sealed record TaskTrackingModeOption(string Key, string Name);

public sealed record TaskTrackingDetail(
    string SourceTaskId,
    string Name,
    string TraderName,
    string MapName,
    string Description,
    string TaskImageUrl,
    int Experience,
    IReadOnlyList<TaskTrackingObjective> Objectives,
    IReadOnlyList<TaskTrackingRelatedTask> PreviousTasks,
    IReadOnlyList<TaskTrackingRelatedTask> NextTasks,
    IReadOnlyList<TaskTrackingRewardItem> ItemRewards,
    IReadOnlyList<TaskTrackingOfferUnlock> OfferUnlocks,
    IReadOnlyList<string> OtherRewards,
    IReadOnlyList<TaskTrackingRewardItem> NeededKeys,
    string RewardSummary,
    string GuideMarkdown,
    string VideoLink);

public sealed record TaskTrackingObjective(string Description, double Count, bool Optional)
{
    public string Summary => Count > 1
        ? $"{Description}（{Count:0.##}）"
        : Description;
}

public sealed record TaskTrackingRelatedTask(string SourceTaskId, string Name);

public sealed record TaskTrackingRewardItem(string ItemId, string Name, double Count, string IconUrl)
{
    public string Summary => Count > 0 ? $"{Count:0.##}× {Name}" : Name;
}

public sealed record TaskTrackingOfferUnlock(string ItemId, string Name, string TraderName, int Level, string IconUrl)
{
    public string Summary => string.IsNullOrWhiteSpace(TraderName)
        ? $"解锁购买：{Name}"
        : $"{TraderName} 等级 {Level}：{Name}";
}

public sealed record TaskTrackingDetailLoadResult(TaskTrackingDetail? Detail, string? ErrorMessage)
{
    public bool IsAvailable => Detail is not null;
}
