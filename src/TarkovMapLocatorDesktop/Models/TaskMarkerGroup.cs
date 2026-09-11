namespace TarkovMapLocatorDesktop.Models;

public sealed record TaskWorldPoint(double X, double Y, double Z, string Objective);

public sealed record TaskMarkerGroup(
    string MapKey,
    string TaskName,
    IReadOnlyList<TaskWorldPoint> Points,
    IReadOnlyList<string> Objectives)
{
    public string SelectionKey => $"{MapKey}\u001f{TaskName}";
    public string Detail => Objectives.Count switch
    {
        0 => "任务目标点",
        1 => Objectives[0],
        _ => $"{Objectives[0]} 等 {Objectives.Count} 个目标"
    };
}
