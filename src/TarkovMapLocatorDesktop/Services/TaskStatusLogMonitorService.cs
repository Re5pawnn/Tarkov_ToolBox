using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Incrementally reads EFT push-notification logs and converts quest messages
/// into the task-tree status model. The parsing follows the game's structured
/// ChatMessageReceived records; no game files are modified.
/// </summary>
public sealed class TaskStatusLogMonitorService
{
    private const int MaxHistoryFileCount = 256;
    private const long MaxHistoryBytes = 32L * 1024 * 1024;
    private const int MaxChunkBytes = 1024 * 1024;
    private const int MaxPollBytes = 4 * MaxChunkBytes;
    private const int TailCharacterCount = 16 * 1024;

    private static readonly Regex LogDirectoryRegex = new(
        @"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{2}-\d{2})_[0-9.]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TaskMessageRegex = new(
        "\\\"text\\\"\\s*:\\s*\\\"quest started\\\"[\\s\\S]{0,8192}?" +
        "\\\"templateId\\\"\\s*:\\s*\\\"(?<id>[0-9a-f]{24})\\s+" +
        "(?<kind>description|successMessageText|failMessageText)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, string[]> _trackingIdsByGameId;
    private readonly Dictionary<string, TaskTrackingStatus> _detectedStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _detectedGameTaskIds = new(StringComparer.Ordinal);
    private string? _cachedLogsRoot;
    private string? _cachedFilePath;
    private long _cachedReadOffset;
    private string _parseTail = "";
    private bool _initialized;
    private string _activeMode = "pve";

    internal TaskStatusLogMonitorService(IReadOnlyDictionary<string, string[]> trackingIdsByGameId)
    {
        _trackingIdsByGameId = trackingIdsByGameId;
    }

    public void SetMode(string mode)
    {
        var normalized = NormalizeMode(mode);
        if (normalized == _activeMode) return;
        _activeMode = normalized;
        Reset();
    }

    public IReadOnlyList<DetectedTaskStatusChange> ReadLatest(
        string? configuredPath,
        bool replayHistoryWhenUninitialized = true)
    {
        var logsRoot = ResolveLogsRoot(configuredPath);
        if (logsRoot is null)
        {
            Reset();
            return [];
        }

        try
        {
            var pathChanged = !string.Equals(_cachedLogsRoot, logsRoot, StringComparison.OrdinalIgnoreCase);
            if (!_initialized || pathChanged)
            {
                Reset();
                _cachedLogsRoot = logsRoot;
                _initialized = true;
                if (replayHistoryWhenUninitialized) return ReadHistory(logsRoot);
                PrimeToLatest(logsRoot);
                return [];
            }

            var latest = FindLatestNotificationFile(logsRoot);
            if (latest is null) return [];
            if (!string.Equals(_cachedFilePath, latest.FullName, StringComparison.OrdinalIgnoreCase) || latest.Length < _cachedReadOffset)
            {
                _cachedFilePath = latest.FullName;
                _cachedReadOffset = 0;
                _parseTail = "";
            }

            return ReadIncremental(latest, MaxPollBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RuntimeLogService.Warning("任务识别", "读取任务状态日志失败", exception.Message);
            return [];
        }
    }

    /// <summary>
    /// Replays historical task messages only after an explicit user sync. Game
    /// logs do not carry the selected PVP/PVE/season profile, so automatic mode
    /// switches must never call this path.
    /// </summary>
    public IReadOnlyList<DetectedTaskStatusChange> ReplayHistory(string? configuredPath)
    {
        var logsRoot = ResolveLogsRoot(configuredPath);
        if (logsRoot is null)
        {
            Reset();
            return [];
        }

        try
        {
            Reset();
            _cachedLogsRoot = logsRoot;
            _initialized = true;
            return ReadHistory(logsRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RuntimeLogService.Warning("任务识别", "重放任务状态日志失败", exception.Message);
            return [];
        }
    }

    private void PrimeToLatest(string logsRoot)
    {
        var latest = FindLatestNotificationFile(logsRoot);
        if (latest is null) return;
        latest.Refresh();
        _cachedFilePath = latest.FullName;
        _cachedReadOffset = latest.Length;
        _parseTail = "";
    }

    private IReadOnlyList<DetectedTaskStatusChange> ReadHistory(string logsRoot)
    {
        var files = EnumerateNotificationFiles(logsRoot).ToArray();
        if (files.Length == 0) return [];

        var selected = new List<FileInfo>();
        long selectedBytes = 0;
        for (var index = files.Length - 1; index >= 0 && selected.Count < MaxHistoryFileCount; index--)
        {
            var file = files[index];
            if (selected.Count > 0 && selectedBytes + file.Length > MaxHistoryBytes) break;
            selected.Add(file);
            selectedBytes += file.Length;
        }
        selected.Reverse();

        var before = new Dictionary<string, TaskTrackingStatus?>(StringComparer.Ordinal);
        foreach (var file in selected)
        {
            _parseTail = "";
            var offset = 0L;
            while (offset < file.Length)
            {
                var chunk = LocalRaidMonitorService.ReadLogChunk(file.FullName, offset, MaxChunkBytes, dropFirstPartialLine: false);
                if (chunk.NextOffset <= offset) break;
                if (chunk.SkippedPartialLine) _parseTail = "";
                ApplyEvents(chunk.Text, file.Name, before);
                offset = chunk.NextOffset;
            }

            if (ReferenceEquals(file, selected[^1]))
            {
                _cachedFilePath = file.FullName;
                _cachedReadOffset = offset;
            }
        }

        return BuildChanges(before, selected[^1].Name);
    }

    private IReadOnlyList<DetectedTaskStatusChange> ReadIncremental(FileInfo file, int budget)
    {
        file.Refresh();
        if (_cachedReadOffset >= file.Length) return [];

        var before = new Dictionary<string, TaskTrackingStatus?>(StringComparer.Ordinal);
        var remaining = budget;
        while (remaining > 0 && _cachedReadOffset < file.Length)
        {
            var chunk = LocalRaidMonitorService.ReadLogChunk(file.FullName, _cachedReadOffset, Math.Min(MaxChunkBytes, remaining), dropFirstPartialLine: false);
            if (chunk.NextOffset <= _cachedReadOffset) break;
            if (chunk.SkippedPartialLine) _parseTail = "";
            var consumed = chunk.NextOffset - _cachedReadOffset;
            _cachedReadOffset = chunk.NextOffset;
            remaining -= (int)Math.Min(consumed, int.MaxValue);
            ApplyEvents(chunk.Text, file.Name, before);
            file.Refresh();
        }

        return BuildChanges(before, file.Name);
    }

    private void ApplyEvents(
        string chunk,
        string sourceFileName,
        IDictionary<string, TaskTrackingStatus?> before)
    {
        var text = _parseTail + chunk;
        foreach (Match match in TaskMessageRegex.Matches(text))
        {
            var gameTaskId = match.Groups["id"].Value.ToLowerInvariant();
            if (!_trackingIdsByGameId.TryGetValue(gameTaskId, out var trackingIds)) continue;
            if (!TryResolveStatus(match.Groups["kind"].Value, out var status)) continue;

            foreach (var trackingId in trackingIds)
            {
                if (!IsTrackingIdForActiveMode(trackingId)) continue;
                if (!before.ContainsKey(trackingId))
                    before[trackingId] = _detectedStatuses.TryGetValue(trackingId, out var previous) ? previous : null;
                _detectedStatuses[trackingId] = status;
                _detectedGameTaskIds[trackingId] = gameTaskId;
            }
        }

        _parseTail = text.Length <= TailCharacterCount ? text : text[^TailCharacterCount..];
    }

    private IReadOnlyList<DetectedTaskStatusChange> BuildChanges(
        IReadOnlyDictionary<string, TaskTrackingStatus?> before,
        string sourceFileName)
    {
        if (before.Count == 0) return [];
        var now = DateTimeOffset.Now;
        return before
            .Where(pair => _detectedStatuses.TryGetValue(pair.Key, out var current) && pair.Value != current)
            .Select(pair =>
            {
                var status = _detectedStatuses[pair.Key];
                var gameTaskId = _detectedGameTaskIds.GetValueOrDefault(pair.Key) ?? "";
                return new DetectedTaskStatusChange(pair.Key, status, gameTaskId, now, sourceFileName);
            })
            .ToArray();
    }

    internal static bool TryResolveStatus(string templateKind, out TaskTrackingStatus status)
    {
        if (templateKind.Equals("description", StringComparison.OrdinalIgnoreCase))
        {
            status = TaskTrackingStatus.Accepted;
            return true;
        }
        if (templateKind.Equals("successMessageText", StringComparison.OrdinalIgnoreCase))
        {
            status = TaskTrackingStatus.Completed;
            return true;
        }
        if (templateKind.Equals("failMessageText", StringComparison.OrdinalIgnoreCase))
        {
            status = TaskTrackingStatus.NotStarted;
            return true;
        }

        status = default;
        return false;
    }

    private bool IsTrackingIdForActiveMode(string trackingId) => _activeMode switch
    {
        "pvp" => trackingId.StartsWith("pvp:", StringComparison.Ordinal),
        "season" => trackingId.StartsWith("season:", StringComparison.Ordinal),
        _ => !trackingId.StartsWith("pvp:", StringComparison.Ordinal) &&
             !trackingId.StartsWith("season:", StringComparison.Ordinal)
    };

    private static string NormalizeMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "pvp" => "pvp",
        "season" => "season",
        _ => "pve"
    };

    private static IEnumerable<FileInfo> EnumerateNotificationFiles(string logsRoot) =>
        Directory.EnumerateDirectories(logsRoot)
            .Select(path => new { Path = path, Stamp = ParseLogStamp(Path.GetFileName(path)) })
            .Where(item => item.Stamp.HasValue)
            .OrderBy(item => item.Stamp)
            .SelectMany(item => Directory.EnumerateFiles(item.Path, "*push-notifications*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc));

    private static FileInfo? FindLatestNotificationFile(string logsRoot)
    {
        var latestDirectory = Directory.EnumerateDirectories(logsRoot)
            .Select(path => new { Path = path, Stamp = ParseLogStamp(Path.GetFileName(path)) })
            .Where(item => item.Stamp.HasValue)
            .OrderByDescending(item => item.Stamp)
            .FirstOrDefault();
        return latestDirectory is null
            ? null
            : Directory.EnumerateFiles(latestDirectory.Path, "*push-notifications*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();
    }

    private static string? ResolveLogsRoot(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Directory.Exists(configuredPath)) return null;
        var configured = configuredPath.Trim();
        if (ParseLogStamp(Path.GetFileName(configured)).HasValue)
            return Directory.GetParent(configured)?.FullName;
        if (string.Equals(Path.GetFileName(configured), "Logs", StringComparison.OrdinalIgnoreCase)) return configured;
        var logs = Path.Combine(configured, "Logs");
        return Directory.Exists(logs) ? logs : null;
    }

    private static DateTime? ParseLogStamp(string name)
    {
        var match = LogDirectoryRegex.Match(name);
        return match.Success && DateTime.TryParseExact(
            match.Groups["stamp"].Value,
            "yyyy.MM.dd_H-mm-ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var value)
                ? value
                : null;
    }

    private void Reset()
    {
        _cachedLogsRoot = null;
        _cachedFilePath = null;
        _cachedReadOffset = 0;
        _parseTail = "";
        _detectedStatuses.Clear();
        _detectedGameTaskIds.Clear();
        _initialized = false;
    }
}
