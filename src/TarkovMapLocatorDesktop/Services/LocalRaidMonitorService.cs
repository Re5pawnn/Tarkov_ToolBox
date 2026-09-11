using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Read-only monitor for the desktop-selected local paths, with the original
/// application's paths used only as a legacy fallback.
/// Logs select the map; screenshot file names only provide the player's coordinates.
/// </summary>
public sealed class LocalRaidMonitorService : IDisposable
{
    private const int MaxLogReadBytes = 1024 * 1024;
    private const int MaxInitialLogReadBytes = 8 * MaxLogReadBytes;
    private static readonly TimeSpan ScreenshotRecoveryScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ScreenshotWriteTimeToleranceBeforeName = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScreenshotWriteTimeToleranceAfterName = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan CoordinateFutureTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RoutePositiveEvidenceWindow = TimeSpan.FromMinutes(2);

    private static readonly Regex ScreenshotNameRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\[(?<hour>\d{2})-(?<minute>\d{2})\]_" +
        @"(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)_" +
        @"(?<qx>-?\d+(?:\.\d+)?),\s*(?<qy>-?\d+(?:\.\d+)?),\s*" +
        @"(?<qz>-?\d+(?:\.\d+)?),\s*(?<qw>-?\d+(?:\.\d+)?)_" +
        @"(?<scale>-?\d+(?:\.\d+)?)(?:\s*\((?<index>\d+)\))?\.png$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LogDirectoryRegex = new(
        @"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{2}-\d{2})_[0-9.]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ScenePresetRegex = new(
        @"scene\s+preset\s+path\s*:\s*maps/(?<bundle>[a-zA-Z0-9_\-]+)\.bundle(?:\s+rcid:(?<rcid>[a-zA-Z0-9_.\-]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TransitLocationRegex = new(
        // Route records commonly end after the source map ("Woods ->") even
        // during an ordinary map load.  Some versions can also include a
        // destination ("Woods -> Customs").
        // Keep whitespace matching on the same physical log line.  Using \s here
        // lets the expression cross CR/LF and consume the next record's date as
        // a fake destination (for example "Interchange ->\r\n2026-08-10").
        @"\bLocations:[ \t]*(?<source>[a-zA-Z0-9_\-]+)[ \t]*->[ \t]*(?<target>[a-zA-Z0-9_\-]+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NetworkGameLocationRegex = new(
        // Do not accept every "Location:" field in application.log.  Logs also
        // use that word for non-raid diagnostics, which used to make a later
        // unrelated line switch the map.  NetworkGameCreate is the record that
        // establishes the active raid location.
        @"\bTRACE-NetworkGameCreate\b[^\r\n]*?\bLocation:\s*(?<location>[a-zA-Z0-9_\-]+)(?=\s*,|\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> MapAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tarkovstreets"] = "streets-of-tarkov",
        ["streets"] = "streets-of-tarkov",
        ["streets_preset"] = "streets-of-tarkov",
        ["city"] = "streets-of-tarkov",
        ["city_preset"] = "streets-of-tarkov",
        ["sandbox"] = "ground-zero",
        ["sandbox_high"] = "ground-zero",
        ["groundzero"] = "ground-zero",
        ["ground-zero"] = "ground-zero",
        ["bigmap"] = "customs",
        ["customs"] = "customs",
        ["customs_preset"] = "customs",
        ["factory4_day"] = "factory",
        ["factory4_night"] = "factory",
        ["factory_day"] = "factory",
        ["factory_night"] = "factory",
        ["factory_day_preset"] = "factory",
        ["factory_night_preset"] = "factory",
        ["factory"] = "factory",
        ["shopping_mall"] = "interchange",
        ["interchange"] = "interchange",
        ["laboratory"] = "the-lab",
        ["laboratory_preset"] = "the-lab",
        ["laboratory_dark"] = "the-labyrinth",
        ["laboratory_dark_preset"] = "the-labyrinth",
        ["lighthouse"] = "lighthouse",
        ["rezervbase"] = "reserve",
        ["reserve"] = "reserve",
        ["shoreline"] = "shoreline",
        ["woods"] = "woods",
        ["labyrinth"] = "the-labyrinth",
        ["terminal"] = "terminal",
        ["terminal_preset"] = "terminal",
        ["icebreaker"] = "icebreaker",
        ["icebreaker_preset"] = "icebreaker"
    };

    private string? _cachedLogFile;
    private long _cachedLogLength;
    private long _cachedLogReadOffset;
    private DateTime _cachedLogModifiedUtc;
    private string? _cachedMapKey;
    private string? _cachedMapSource;
    private string? _cachedMapDetail;
    private DateTimeOffset? _cachedMapObservedAt;
    private DateTimeOffset? _cachedGameStartAt;
    private DateTimeOffset? _cachedRaidEndAt;
    private bool _cachedMapLogCaughtUp;
    private bool _cachedTransitDestinationPending;
    private string? _cachedScreenshotDirectory;
    private DateTime _cachedScreenshotDirectoryModifiedUtc;
    private LiveCoordinate? _cachedCoordinate;
    private readonly ConcurrentQueue<string> _pendingScreenshotFiles = new();
    private FileSystemWatcher? _screenshotWatcher;
    private string? _watchedScreenshotDirectory;
    private DateTime _lastScreenshotFullScanUtc;
    private int _screenshotRescanRequired = 1;
    private int _screenshotWatcherRestartRequired;

    public LocalRaidSnapshot ReadLatest()
    {
        var paths = ReadConfiguredPaths();
        var coordinate = ReadLatestCoordinate(paths.ScreenshotPath);
        var (mapKey, mapSource, mapDetail, latestLogFileName, gameStartAt, raidEndAt, mapObservedAt, isMapLogCaughtUp, isTransitDestinationPending) = ReadLatestMap(paths.GamePath);
        var hasConfiguredPaths = !string.IsNullOrWhiteSpace(paths.ScreenshotPath) || !string.IsNullOrWhiteSpace(paths.GamePath);

        var status = !isMapLogCaughtUp
            ? "正在追赶游戏日志，暂不应用历史地图记录"
            : isTransitDestinationPending
                ? "检测到转移记录，正在等待目标地图日志确认"
            : mapKey is not null && coordinate is not null
            ? "日志已锁定地图，截图只更新当前位置"
            : mapKey is not null
                ? "日志已锁定地图，等待含坐标截图"
                : coordinate is not null
                    ? "已读取截图坐标，等待游戏日志确认地图"
                    : hasConfiguredPaths
                        ? "等待新的游戏日志或坐标截图"
                        : "未读取到本机路径配置";

        return new LocalRaidSnapshot(
            mapKey,
            mapSource,
            mapDetail,
            coordinate,
            status,
            hasConfiguredPaths,
            paths.ScreenshotPath,
            paths.GamePath,
            latestLogFileName,
            gameStartAt,
            raidEndAt,
            mapObservedAt,
            isMapLogCaughtUp,
            isTransitDestinationPending);
    }

    private static (string? ScreenshotPath, string? GamePath) ReadConfiguredPaths()
    {
        var desktop = DesktopPreferencesService.Load();
        var original = ReadOriginalConfiguredPaths();
        return (
            desktop.ScreenshotDirectory ?? original.ScreenshotPath,
            desktop.GameLogDirectory ?? original.GamePath);
    }

    private static (string? ScreenshotPath, string? GamePath) ReadOriginalConfiguredPaths()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMapLocator", "local-paths.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return (ReadString(root, "screenshot"), ReadString(root, "game"));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    internal LiveCoordinate? ReadLatestCoordinate(string? screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath) || !Directory.Exists(screenshotPath))
        {
            ResetScreenshotCache();
            return null;
        }

        try
        {
            var directory = new DirectoryInfo(screenshotPath);
            var directoryPath = directory.FullName;
            if (Interlocked.Exchange(ref _screenshotWatcherRestartRequired, 0) != 0)
                DisposeScreenshotWatcher();
            EnsureScreenshotWatcher(directoryPath);
            var pathChanged = !string.Equals(_cachedScreenshotDirectory, directoryPath, StringComparison.OrdinalIgnoreCase);
            var requiresFullScan = pathChanged ||
                                   Interlocked.Exchange(ref _screenshotRescanRequired, 0) != 0 ||
                                   DateTime.UtcNow - _lastScreenshotFullScanUtc >= ScreenshotRecoveryScanInterval;
            var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_pendingScreenshotFiles.TryDequeue(out var changedPath)) changedFiles.Add(changedPath);

            // FileSystemWatcher can legally coalesce or lose events without
            // raising Error.  A directory timestamp change with an empty queue
            // is therefore a cheap signal to recover with a complete scan.
            if (!requiresFullScan &&
                directory.LastWriteTimeUtc != _cachedScreenshotDirectoryModifiedUtc)
                requiresFullScan = true;

            var latest = requiresFullScan ? null : _cachedCoordinate;
            IReadOnlyList<string> candidates;
            if (requiresFullScan)
            {
                // Materialise after draining old notifications.  Notifications
                // arriving while enumeration is in progress remain queued for
                // the next poll instead of being cleared and lost.
                candidates = Directory.EnumerateFiles(directoryPath, "*.png", SearchOption.TopDirectoryOnly).ToArray();
                _lastScreenshotFullScanUtc = DateTime.UtcNow;
            }
            else
            {
                if (changedFiles.Count == 0) return _cachedCoordinate;
                candidates = changedFiles.ToArray();
            }

            foreach (var filePath in candidates)
            {
                var coordinate = ParseScreenshot(new FileInfo(filePath));
                if (coordinate is not null && (latest is null || coordinate.SortOrder > latest.SortOrder ||
                    coordinate.SortOrder == latest.SortOrder && coordinate.CapturedAt > latest.CapturedAt))
                {
                    latest = coordinate;
                }
            }

            directory.Refresh();
            _cachedScreenshotDirectory = directoryPath;
            _cachedScreenshotDirectoryModifiedUtc = directory.LastWriteTimeUtc;
            _cachedCoordinate = latest;
            return latest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ResetScreenshotCache();
            return null;
        }
    }

    /// <summary>
    /// Forces the next polling pass to reconcile the screenshot directory.
    /// Restarting the watcher is useful after monitoring has been stopped and
    /// resumed because FileSystemWatcher may silently lose notifications.
    /// </summary>
    internal void RequestScreenshotRescan(bool restartWatcher = false)
    {
        Interlocked.Exchange(ref _screenshotRescanRequired, 1);
        if (restartWatcher)
            Interlocked.Exchange(ref _screenshotWatcherRestartRequired, 1);
    }

    private void ResetScreenshotCache()
    {
        _cachedScreenshotDirectory = null;
        _cachedScreenshotDirectoryModifiedUtc = DateTime.MinValue;
        _cachedCoordinate = null;
        _lastScreenshotFullScanUtc = DateTime.MinValue;
        DisposeScreenshotWatcher();
    }

    private void EnsureScreenshotWatcher(string directoryPath)
    {
        if (string.Equals(_watchedScreenshotDirectory, directoryPath, StringComparison.OrdinalIgnoreCase) && _screenshotWatcher is not null)
            return;

        DisposeScreenshotWatcher();
        var watcher = new FileSystemWatcher(directoryPath, "*.png")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            InternalBufferSize = 16 * 1024
        };
        watcher.Created += ScreenshotWatcher_Changed;
        watcher.Changed += ScreenshotWatcher_Changed;
        watcher.Renamed += ScreenshotWatcher_Renamed;
        watcher.Error += ScreenshotWatcher_Error;
        watcher.EnableRaisingEvents = true;
        _screenshotWatcher = watcher;
        _watchedScreenshotDirectory = directoryPath;
        Interlocked.Exchange(ref _screenshotRescanRequired, 1);
    }

    private void ScreenshotWatcher_Changed(object sender, FileSystemEventArgs e) => _pendingScreenshotFiles.Enqueue(e.FullPath);

    private void ScreenshotWatcher_Renamed(object sender, RenamedEventArgs e) => _pendingScreenshotFiles.Enqueue(e.FullPath);

    private void ScreenshotWatcher_Error(object sender, ErrorEventArgs e) => Interlocked.Exchange(ref _screenshotRescanRequired, 1);

    private void DisposeScreenshotWatcher()
    {
        var watcher = _screenshotWatcher;
        _screenshotWatcher = null;
        _watchedScreenshotDirectory = null;
        if (watcher is null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Created -= ScreenshotWatcher_Changed;
        watcher.Changed -= ScreenshotWatcher_Changed;
        watcher.Renamed -= ScreenshotWatcher_Renamed;
        watcher.Error -= ScreenshotWatcher_Error;
        watcher.Dispose();
        while (_pendingScreenshotFiles.TryDequeue(out _)) { }
    }

    private static LiveCoordinate? ParseScreenshot(FileInfo file)
    {
        var match = ScreenshotNameRegex.Match(file.Name);
        if (!match.Success || !file.Exists) return null;

        if (!TryReadDouble(match, "x", out var x) || !TryReadDouble(match, "y", out var y) || !TryReadDouble(match, "z", out var z) ||
            !TryReadDouble(match, "qx", out var qx) || !TryReadDouble(match, "qy", out var qy) ||
            !TryReadDouble(match, "qz", out var qz) || !TryReadDouble(match, "qw", out var qw)) return null;

        var index = int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex) ? parsedIndex : 0;
        if (!DateTime.TryParseExact(
                $"{match.Groups["date"].Value}T{match.Groups["hour"].Value}:{match.Groups["minute"].Value}:00",
                "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp)) return null;

        var coordinateTimestamp = new DateTimeOffset(stamp);
        var fileWriteTimestamp = new DateTimeOffset(file.LastWriteTime);
        // The filename only has minute precision. LastWriteTime supplies the
        // missing seconds while it is still consistent with that minute; copied
        // or restored files keep the embedded timestamp instead of pretending to
        // be a new in-raid screenshot.
        var capturedAt = fileWriteTimestamp >= coordinateTimestamp - ScreenshotWriteTimeToleranceBeforeName &&
                         fileWriteTimestamp <= coordinateTimestamp + ScreenshotWriteTimeToleranceAfterName
            ? fileWriteTimestamp
            : coordinateTimestamp + TimeSpan.FromSeconds(30);
        if (!IsPlausibleCoordinateTime(capturedAt, DateTimeOffset.Now)) return null;
        var sinY = 2 * (qw * qy + qx * qz);
        var cosY = 1 - 2 * (qy * qy + qz * qz);
        var yaw = (Math.Atan2(sinY, cosY) * 180 / Math.PI + 360) % 360;
        return new LiveCoordinate(
            file.Name,
            x,
            y,
            z,
            yaw,
            capturedAt,
            coordinateTimestamp.ToUnixTimeMilliseconds() * 1000L + index,
            coordinateTimestamp);
    }

    internal (string? MapKey, string? MapSource, string? MapDetail, string? LatestLogFileName, DateTimeOffset? GameStartAt, DateTimeOffset? RaidEndAt, DateTimeOffset? MapObservedAt, bool IsMapLogCaughtUp, bool IsTransitDestinationPending) ReadLatestMap(string? configuredPath)
    {
        try
        {
            var logDirectory = ResolveLatestLogDirectory(configuredPath);
            if (logDirectory is null) return (null, null, "未找到可读取的 Logs/log_* 目录", null, null, null, null, true, false);
            var file = Directory.EnumerateFiles(logDirectory, "*application*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null || !file.Exists) return (null, null, "当前日志目录中没有 application 日志", null, null, null, null, true, false);

            if (string.Equals(_cachedLogFile, file.FullName, StringComparison.OrdinalIgnoreCase) &&
                _cachedLogLength == file.Length && _cachedLogModifiedUtc == file.LastWriteTimeUtc &&
                _cachedLogReadOffset >= file.Length)
            {
                return (_cachedMapKey, _cachedMapSource, _cachedMapDetail, file.Name, _cachedGameStartAt, _cachedRaidEndAt, _cachedMapObservedAt, _cachedMapLogCaughtUp, _cachedTransitDestinationPending);
            }

            var samePath = string.Equals(_cachedLogFile, file.FullName, StringComparison.OrdinalIgnoreCase);
            var isSameLogFile = samePath && file.Length >= _cachedLogReadOffset &&
                                !(file.Length == _cachedLogLength && file.LastWriteTimeUtc != _cachedLogModifiedUtc);
            if (!isSameLogFile)
            {
                _cachedMapKey = null;
                _cachedMapSource = null;
                _cachedMapDetail = null;
                _cachedMapObservedAt = null;
                _cachedGameStartAt = null;
                _cachedRaidEndAt = null;
                _cachedMapLogCaughtUp = false;
                _cachedTransitDestinationPending = false;
                _cachedLogReadOffset = 0;
            }

            // Never jump to the file tail: a map token or GameStarted line must not be
            // discarded merely because the log grew faster than one polling interval.
            // A new file gets an 8 MiB discovery budget, then every later poll continues
            // from the last complete line with a bounded 1 MiB budget.
            var readOffset = isSameLogFile ? _cachedLogReadOffset : 0L;
            var remainingBudget = isSameLogFile ? MaxLogReadBytes : MaxInitialLogReadBytes;
            var mapKey = _cachedMapKey;
            var mapSource = _cachedMapSource;
            var mapDetail = _cachedMapDetail;
            var mapObservedAt = _cachedMapObservedAt;
            var gameStartAt = _cachedGameStartAt;
            var raidEndAt = _cachedRaidEndAt;
            var transitDestinationPending = _cachedTransitDestinationPending;

            while (remainingBudget > 0 && readOffset < file.Length)
            {
                var maxBytes = Math.Min(MaxLogReadBytes, remainingBudget);
                var chunk = ReadLogChunk(file.FullName, readOffset, maxBytes, dropFirstPartialLine: false);
                if (chunk.NextOffset <= readOffset) break;

                var resolved = ResolveLatestMapTokenWithRaidState(
                    chunk.Text,
                    gameStartAt,
                    raidEndAt,
                    mapKey,
                    mapSource,
                    mapObservedAt);
                var timing = ResolveRaidTiming(chunk.Text, gameStartAt, raidEndAt);
                if (resolved.MapKey is not null)
                {
                    mapKey = resolved.MapKey;
                    mapSource = resolved.MapSource;
                    mapDetail = resolved.Detail;
                    mapObservedAt = resolved.MapObservedAt ?? mapObservedAt;
                    transitDestinationPending = resolved.IsTransitDestinationPending;
                }
                else if (resolved.InvalidatesExistingMap)
                {
                    mapKey = null;
                    mapSource = resolved.MapSource;
                    mapDetail = resolved.Detail;
                    mapObservedAt = resolved.MapObservedAt;
                    transitDestinationPending = false;
                }
                else if (resolved.IsTransitDestinationPending)
                {
                    transitDestinationPending = true;
                    mapDetail = resolved.Detail;
                }
                else if (resolved.RecordCount > 0 || mapDetail is null)
                {
                    mapDetail = resolved.Detail;
                }

                gameStartAt = timing.GameStartAt;
                raidEndAt = timing.RaidEndAt;
                var consumed = chunk.NextOffset - readOffset;
                readOffset = chunk.NextOffset;
                remainingBudget -= (int)Math.Min(consumed, int.MaxValue);
            }

            // A monitor may be started in the middle of a large log.  Never
            // expose the latest token from the first chunk as the current map:
            // it can be a location from much earlier in the same raid.
            file.Refresh();
            var isMapLogCaughtUp = readOffset >= file.Length;

            _cachedLogFile = file.FullName;
            _cachedLogLength = file.Length;
            _cachedLogReadOffset = readOffset;
            _cachedLogModifiedUtc = file.LastWriteTimeUtc;
            _cachedMapKey = mapKey;
            _cachedMapSource = mapSource;
            _cachedMapDetail = mapDetail;
            _cachedMapObservedAt = mapObservedAt;
            _cachedGameStartAt = gameStartAt;
            _cachedRaidEndAt = raidEndAt;
            _cachedMapLogCaughtUp = isMapLogCaughtUp;
            _cachedTransitDestinationPending = transitDestinationPending;
            return (_cachedMapKey, _cachedMapSource, _cachedMapDetail, file.Name, _cachedGameStartAt, _cachedRaidEndAt, _cachedMapObservedAt, _cachedMapLogCaughtUp, _cachedTransitDestinationPending);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null, "读取游戏日志失败：目录正在被占用或不可访问", null, null, null, null, true, false);
        }
    }

    private static string? ResolveLatestLogDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Directory.Exists(configuredPath)) return null;
        var configured = configuredPath.Trim();
        if (ParseLogStamp(Path.GetFileName(configured)).HasValue) return configured;

        var logsRoot = string.Equals(Path.GetFileName(configured), "Logs", StringComparison.OrdinalIgnoreCase)
            ? configured
            : Path.Combine(configured, "Logs");
        if (!Directory.Exists(logsRoot)) return null;

        return Directory.EnumerateDirectories(logsRoot)
            .Select(path => new { Path = path, Stamp = ParseLogStamp(Path.GetFileName(path)) })
            .Where(item => item.Stamp.HasValue)
            .OrderBy(item => item.Stamp)
            .LastOrDefault()?.Path;
    }

    private static DateTime? ParseLogStamp(string name)
    {
        var match = LogDirectoryRegex.Match(name);
        return match.Success && DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyy.MM.dd_H-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
    }

    internal static LogChunk ReadLogChunk(string path, long requestedOffset, int maxBytes, bool dropFirstPartialLine)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Clamp(requestedOffset, 0, stream.Length);
        var startsInsideLine = dropFirstPartialLine;
        if (offset > 0)
        {
            stream.Position = offset - 1;
            startsInsideLine |= stream.ReadByte() != '\n';
        }
        stream.Position = offset;
        var count = checked((int)Math.Min(maxBytes, stream.Length - offset));
        if (count <= 0) return new LogChunk("", offset);

        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var current = stream.Read(buffer, read, count - read);
            if (current == 0) break;
            read += current;
        }
        if (read == 0) return new LogChunk("", offset);
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
        if (lastNewline < 0)
        {
            var skipOversizedLine = startsInsideLine || read >= maxBytes;
            return new LogChunk("", skipOversizedLine ? offset + read : offset, skipOversizedLine);
        }

        var start = 0;
        if (startsInsideLine)
        {
            var firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, lastNewline + 1);
            if (firstNewline < 0) return new LogChunk("", offset + lastNewline + 1, true);
            start = firstNewline + 1;
        }
        var text = Encoding.UTF8.GetString(buffer, start, lastNewline + 1 - start);
        return new LogChunk(text, offset + lastNewline + 1, startsInsideLine);
    }

    private static (DateTimeOffset? GameStartAt, DateTimeOffset? RaidEndAt) ResolveRaidTiming(
        string text,
        DateTimeOffset? previousGameStartAt,
        DateTimeOffset? previousRaidEndAt)
    {
        var gameStartAt = previousGameStartAt;
        var raidEndAt = previousRaidEndAt;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('|');
            if (separator <= 0) continue;

            var message = line[(separator + 1)..];
            var isStart = message.Contains("application|GameStarted", StringComparison.OrdinalIgnoreCase);
            var isEnd = message.Contains("Got notification | UserMatchOver", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("PrepareSelectedProfileLocally", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("Local game matching cancelled.", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("Network game matching failed", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("Network game matching aborted", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("Network game matching cancelled", StringComparison.OrdinalIgnoreCase);
            if ((!isStart && !isEnd) || !TryParseLogTimestamp(line[..separator], out var timestamp)) continue;

            if (isStart)
            {
                gameStartAt = timestamp;
                raidEndAt = null;
            }
            else if (gameStartAt is { } start && timestamp >= start)
            {
                raidEndAt = timestamp;
            }
        }

        return (gameStartAt, raidEndAt);
    }

    private static bool TryParseLogTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out timestamp);

    internal static (string? MapKey, string? MapSource, string Detail, int RecordCount, DateTimeOffset? MapObservedAt, bool IsTransitDestinationPending, bool InvalidatesExistingMap) ResolveLatestMapToken(string text) =>
        ResolveLatestMapTokenCore(text, null, null, null, null, null);

    private static (string? MapKey, string? MapSource, string Detail, int RecordCount, DateTimeOffset? MapObservedAt, bool IsTransitDestinationPending, bool InvalidatesExistingMap) ResolveLatestMapTokenWithRaidState(
        string text,
        DateTimeOffset? previousGameStartAt,
        DateTimeOffset? previousRaidEndAt,
        string? existingMapKey,
        string? existingMapSource,
        DateTimeOffset? existingMapObservedAt) =>
        ResolveLatestMapTokenCore(text, previousGameStartAt, previousRaidEndAt, existingMapKey, existingMapSource, existingMapObservedAt);

    private static (string? MapKey, string? MapSource, string Detail, int RecordCount, DateTimeOffset? MapObservedAt, bool IsTransitDestinationPending, bool InvalidatesExistingMap) ResolveLatestMapTokenCore(
        string text,
        DateTimeOffset? previousGameStartAt,
        DateTimeOffset? previousRaidEndAt,
        string? existingMapKey,
        string? existingMapSource,
        DateTimeOffset? existingMapObservedAt)
    {
        var mapKey = existingMapKey;
        var source = existingMapSource;
        var mapObservedAt = existingMapObservedAt;
        DateTimeOffset? positiveMapObservedAt = IsPositiveMapEvidenceSource(existingMapSource)
            ? existingMapObservedAt
            : null;
        var transitDestinationPending = false;
        var invalidatesExistingMap = false;
        string? ignoredSourceOnlyRoute = null;
        var sceneMatches = ScenePresetRegex.Matches(text).Cast<Match>().ToArray();
        var transitMatches = TransitLocationRegex.Matches(text).Cast<Match>().ToArray();
        var networkGameMatches = NetworkGameLocationRegex.Matches(text).Cast<Match>().ToArray();
        var matches = sceneMatches
            .Select(match => (Match: match, Kind: "scene"))
            .Concat(transitMatches.Select(match => (Match: match, Kind: "transit")))
            .Concat(networkGameMatches.Select(match => (Match: match, Kind: "network-game")))
            .OrderBy(entry => entry.Match.Index);

        // Scene, transit and network-game records are interleaved in application logs.
        // Resolve the last supported record by its actual file position, not by category.
        foreach (var entry in matches)
        {
            var match = entry.Match;
            var hasTransitDestination = entry.Kind != "transit" ||
                                        (match.Groups["target"].Success && !string.IsNullOrWhiteSpace(match.Groups["target"].Value));
            if (entry.Kind == "transit" && !hasTransitDestination)
            {
                var routeObservedAt = TryGetTimestampAtMatch(text, match.Index, out var parsedRouteTimestamp)
                    ? parsedRouteTimestamp
                    : (DateTimeOffset?)null;
                var timingAtRoute = ResolveRaidTiming(
                    text[..match.Index],
                    previousGameStartAt,
                    previousRaidEndAt);
                var raidIsActive = timingAtRoute.GameStartAt is not null && timingAtRoute.RaidEndAt is null;
                var positiveEvidenceProtectsRoute = positiveMapObservedAt is { } positiveAt &&
                                                    (routeObservedAt is null ||
                                                     routeObservedAt >= positiveAt &&
                                                     routeObservedAt - positiveAt <= RoutePositiveEvidenceWindow) &&
                                                    (timingAtRoute.GameStartAt is not { } activeRaidStart ||
                                                     positiveAt > activeRaidStart);

                // Source-only routes have two meanings in real logs. Before the
                // first GameStarted they are part of an ordinary map load. Once
                // a raid is active they indicate a transfer unless a newer
                // scene/network record has already confirmed the destination.
                if (raidIsActive && !positiveEvidenceProtectsRoute)
                {
                    mapKey = null;
                    source = null;
                    mapObservedAt = null;
                    transitDestinationPending = true;
                    continue;
                }

                // A recent positive scene/network record always wins over a
                // source-only route, including when the two records arrive in
                // different incremental reads before GameStarted.  In current
                // game logs the route source can be a virtual/backend location
                // (for example laboratory_dark) while the loaded scene remains
                // laboratory, so treating it as newer map evidence causes a
                // false Lab -> Labyrinth switch.  Use the source only as the
                // legacy fallback when no recent positive evidence exists.
                if (!positiveEvidenceProtectsRoute)
                {
                    var routeSource = ResolveMapKey(match.Groups["source"].Value);
                    if (routeSource is not null)
                    {
                        mapKey = routeSource;
                        mapObservedAt = routeObservedAt ?? mapObservedAt;
                        source = $"日志路线 {match.Groups["source"].Value}";
                    }
                }
                else
                {
                    ignoredSourceOnlyRoute = match.Groups["source"].Value;
                }
                transitDestinationPending = false;
                continue;
            }

            var transitLocation = entry.Kind == "transit"
                ? match.Groups["target"].Value
                : "";
            var candidate = entry.Kind == "scene"
                ? ResolveMapKey(match.Groups["rcid"].Value) ?? ResolveMapKey(match.Groups["bundle"].Value)
                : entry.Kind == "transit"
                    ? ResolveMapKey(transitLocation)
                    : ResolveMapKey(match.Groups["location"].Value);
            if (candidate is null)
            {
                mapKey = null;
                mapObservedAt = TryGetTimestampAtMatch(text, match.Index, out var unsupportedTimestamp)
                    ? unsupportedTimestamp
                    : mapObservedAt;
                positiveMapObservedAt = null;
                transitDestinationPending = false;
                invalidatesExistingMap = true;
                ignoredSourceOnlyRoute = null;
                source = entry.Kind switch
                {
                    "scene" => $"不支持的日志场景 {match.Groups["bundle"].Value}",
                    "transit" => $"不支持的日志转移目标 {transitLocation}",
                    _ => $"不支持的日志战局 {match.Groups["location"].Value}"
                };
                continue;
            }
            mapKey = candidate;
            mapObservedAt = TryGetTimestampAtMatch(text, match.Index, out var timestamp) ? timestamp : mapObservedAt;
            positiveMapObservedAt = mapObservedAt ?? positiveMapObservedAt;
            transitDestinationPending = false;
            invalidatesExistingMap = false;
            ignoredSourceOnlyRoute = null;
            source = entry.Kind switch
            {
                "scene" => $"日志场景 {match.Groups["bundle"].Value}",
                "transit" => string.IsNullOrWhiteSpace(match.Groups["target"].Value)
                    ? $"日志转移 {match.Groups["source"].Value}"
                    : $"日志转移 {match.Groups["source"].Value} -> {match.Groups["target"].Value}",
                _ => $"日志战局 {match.Groups["location"].Value}"
            };
        }

        var recordCount = sceneMatches.Length + transitMatches.Length + networkGameMatches.Length;
        var detail = transitDestinationPending
            ? "检测到未包含目标地图的转移日志，等待后续场景或战局日志确认目标"
            : ignoredSourceOnlyRoute is not null && mapKey is not null
                ? $"忽略无目标的转移来源 {ignoredSourceOnlyRoute}，保留已确认地图：{source}"
            : mapKey is not null
            ? $"已命中最近的受支持地图标识：{source}"
            : recordCount > 0
                ? $"扫描日志尾部，发现 {recordCount} 条地点记录，但没有受支持的地图标识"
                : "扫描日志尾部，未发现地图加载或地点切换记录";
        return (mapKey, source, detail, recordCount, mapObservedAt, transitDestinationPending, invalidatesExistingMap);
    }

    private static bool IsPositiveMapEvidenceSource(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        !source.StartsWith("日志路线 ", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetTimestampAtMatch(string text, int matchIndex, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (matchIndex < 0 || matchIndex > text.Length) return false;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, matchIndex - 1)) + 1;
        var separator = text.IndexOf('|', lineStart);
        return separator > lineStart && TryParseLogTimestamp(text[lineStart..separator], out timestamp);
    }

    private static string? ResolveMapKey(string? value)
    {
        var token = (value ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (MapAliases.TryGetValue(token, out var mapKey)) return mapKey;

        const string scenePresetAssetSuffix = ".scenespreset.asset";
        if (token.EndsWith(scenePresetAssetSuffix, StringComparison.Ordinal))
        {
            token = token[..^scenePresetAssetSuffix.Length];
            if (MapAliases.TryGetValue(token, out mapKey)) return mapKey;
        }

        if (token.EndsWith("_preset", StringComparison.Ordinal) || token.EndsWith("-preset", StringComparison.Ordinal))
        {
            token = token[..^"_preset".Length];
            if (MapAliases.TryGetValue(token, out mapKey)) return mapKey;
        }

        token = token.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (MapAliases.TryGetValue(token, out mapKey)) return mapKey;

        return null;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()?.Trim()
            : null;

    private static bool TryReadDouble(Match match, string name, out double value) =>
        double.TryParse(match.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    internal static bool IsPlausibleCoordinateTime(DateTimeOffset capturedAt, DateTimeOffset now) =>
        capturedAt <= now + CoordinateFutureTolerance;

    public void Dispose() => DisposeScreenshotWatcher();

    internal readonly record struct LogChunk(string Text, long NextOffset, bool SkippedPartialLine = false);
}
