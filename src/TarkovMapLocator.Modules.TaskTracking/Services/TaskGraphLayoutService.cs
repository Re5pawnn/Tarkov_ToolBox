using System.Windows;
using TarkovMapLocator.Modules.TaskTracking.Models;

namespace TarkovMapLocator.Modules.TaskTracking.Services;

internal static class TaskGraphLayoutService
{
    private const double ColumnGap = 76;
    private const double RowGap = 18;
    private const double HeaderHeight = 44;
    private const double HeaderGap = 18;
    private const double GutterOffset = 18;
    private const double GutterTrackSpacing = 4;
    private const double RowTrackSpacing = 4;

    internal const double NodeWidth = 220;
    internal const double NodeHeight = 78;
    internal const double GraphMargin = 34;

    internal static TaskGraphLayout Build(
        IReadOnlyList<TaskTrackingTask> tasks,
        IReadOnlyList<TaskTrackingLine> _)
    {
        if (tasks.Count == 0)
            return new TaskGraphLayout(new Dictionary<string, Rect>(), [], [], 0, 0);

        var positions = new Dictionary<string, Rect>(StringComparer.Ordinal);
        var headers = new List<TaskGraphGroupHeader>();
        var groups = tasks
            .GroupBy(task => new
            {
                task.GroupOrder,
                Key = string.IsNullOrWhiteSpace(task.GroupKey) ? $"group-{task.GroupOrder}" : task.GroupKey,
                Name = string.IsNullOrWhiteSpace(task.GroupName) ? "任务" : task.GroupName
            })
            .OrderBy(group => group.Key.GroupOrder)
            .ThenBy(group => group.Key.Name, StringComparer.CurrentCulture)
            .ToArray();

        var nodeTop = GraphMargin + HeaderHeight + HeaderGap;
        var maximumRows = 0;
        for (var column = 0; column < groups.Length; column++)
        {
            var group = groups[column];
            var left = GraphMargin + column * (NodeWidth + ColumnGap);
            var orderedTasks = group
                .OrderBy(task => task.Sequence)
                .ThenBy(task => task.Name, StringComparer.CurrentCulture)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .ToArray();
            maximumRows = Math.Max(maximumRows, orderedTasks.Length);
            headers.Add(new TaskGraphGroupHeader(
                group.Key.Key,
                group.Key.Name,
                orderedTasks.Length,
                new Rect(left, GraphMargin, NodeWidth, HeaderHeight)));

            for (var row = 0; row < orderedTasks.Length; row++)
            {
                positions[orderedTasks[row].Id] = new Rect(
                    left,
                    nodeTop + row * (NodeHeight + RowGap),
                    NodeWidth,
                    NodeHeight);
            }
        }

        var width = GraphMargin * 2 + groups.Length * NodeWidth + Math.Max(0, groups.Length - 1) * ColumnGap;
        var height = nodeTop + maximumRows * NodeHeight + Math.Max(0, maximumRows - 1) * RowGap + GraphMargin;
        var segments = BuildRelationSegments(tasks, positions);
        return new TaskGraphLayout(positions, segments, headers, width, height);
    }

    private static IReadOnlyList<TaskGraphLineSegment> BuildRelationSegments(
        IReadOnlyList<TaskTrackingTask> tasks,
        IReadOnlyDictionary<string, Rect> positions)
    {
        var tasksById = tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var segments = new List<TaskGraphLineSegment>();
        var seenRelations = new HashSet<(string From, string To)>();
        var seenSegments = new HashSet<(double X1, double Y1, double X2, double Y2)>();
        var relationIndex = 0;

        foreach (var target in tasks
                     .OrderBy(task => task.GroupOrder)
                     .ThenBy(task => task.Sequence)
                     .ThenBy(task => task.Id, StringComparer.Ordinal))
        {
            foreach (var sourceId in target.PreviousTaskIds)
            {
                if (!tasksById.TryGetValue(sourceId, out var source) ||
                    !positions.TryGetValue(source.Id, out var sourceRect) ||
                    !positions.TryGetValue(target.Id, out var targetRect) ||
                    !seenRelations.Add((source.Id, target.Id)))
                {
                    continue;
                }

                if (source.GroupOrder != target.GroupOrder)
                {
                    var targetIsRight = targetRect.Left > sourceRect.Left;
                    var start = new Point(targetIsRight ? sourceRect.Right : sourceRect.Left, sourceRect.Top + sourceRect.Height / 2);
                    var end = new Point(targetIsRight ? targetRect.Left : targetRect.Right, targetRect.Top + targetRect.Height / 2);
                    var gutterTrack = relationIndex % 3;
                    var sourceGutterX = targetIsRight
                        ? sourceRect.Right + GutterOffset + gutterTrack * GutterTrackSpacing
                        : sourceRect.Left - GutterOffset - gutterTrack * GutterTrackSpacing;
                    var targetGutterX = targetIsRight
                        ? targetRect.Left - GutterOffset - gutterTrack * GutterTrackSpacing
                        : targetRect.Right + GutterOffset + gutterTrack * GutterTrackSpacing;
                    // Every task row shares the same vertical rhythm. Route the
                    // long horizontal leg through the empty gap below the source
                    // row so it cannot appear connected to unrelated cards.
                    var channelY = sourceRect.Bottom + RowGap / 2 + (gutterTrack - 1) * RowTrackSpacing;
                    var sourceGutterAtStart = new Point(sourceGutterX, start.Y);
                    var sourceGutterAtChannel = new Point(sourceGutterX, channelY);
                    var targetGutterAtChannel = new Point(targetGutterX, channelY);
                    var targetGutterAtEnd = new Point(targetGutterX, end.Y);
                    Add(start, sourceGutterAtStart);
                    Add(sourceGutterAtStart, sourceGutterAtChannel);
                    Add(sourceGutterAtChannel, targetGutterAtChannel);
                    Add(targetGutterAtChannel, targetGutterAtEnd);
                    Add(targetGutterAtEnd, end);
                }
                else if (AreAdjacentRows(sourceRect, targetRect))
                {
                    var x = sourceRect.Left + sourceRect.Width / 2;
                    Add(
                        sourceRect.Top < targetRect.Top
                            ? new Point(x, sourceRect.Bottom)
                            : new Point(x, sourceRect.Top),
                        sourceRect.Top < targetRect.Top
                            ? new Point(x, targetRect.Top)
                            : new Point(x, targetRect.Bottom));
                }
                else
                {
                    // Non-adjacent tasks in one column use its right-side gutter;
                    // a direct vertical segment would run through every card in
                    // between and falsely imply extra prerequisite relationships.
                    var channelX = sourceRect.Right + GutterOffset + (relationIndex % 3) * GutterTrackSpacing;
                    var start = new Point(sourceRect.Right, sourceRect.Top + sourceRect.Height / 2);
                    var end = new Point(targetRect.Right, targetRect.Top + targetRect.Height / 2);
                    Add(start, new Point(channelX, start.Y));
                    Add(new Point(channelX, start.Y), new Point(channelX, end.Y));
                    Add(new Point(channelX, end.Y), end);
                }

                relationIndex++;
            }
        }

        return segments;

        static bool AreAdjacentRows(Rect first, Rect second) =>
            Math.Abs(first.Top - second.Top) <= NodeHeight + RowGap + .5;

        void Add(Point start, Point end)
        {
            if (start == end) return;
            var normalized = start.X < end.X || start.X == end.X && start.Y <= end.Y
                ? (start.X, start.Y, end.X, end.Y)
                : (end.X, end.Y, start.X, start.Y);
            if (seenSegments.Add(normalized))
                segments.Add(new TaskGraphLineSegment(start, end));
        }
    }
}

internal sealed record TaskGraphLayout(
    IReadOnlyDictionary<string, Rect> Positions,
    IReadOnlyList<TaskGraphLineSegment> Segments,
    IReadOnlyList<TaskGraphGroupHeader> Groups,
    double Width,
    double Height);

internal sealed record TaskGraphGroupHeader(
    string Key,
    string Name,
    int Count,
    Rect Bounds);

internal readonly record struct TaskGraphLineSegment(Point Start, Point End)
{
    internal bool IsSourceAligned => Start.X == End.X || Start.Y == End.Y;
}
