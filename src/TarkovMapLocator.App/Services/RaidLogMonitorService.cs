using System.Text;
using System.Text.RegularExpressions;
using TarkovMapLocator.Core.Logs;

namespace TarkovMapLocator.App.Services;

public sealed partial class RaidLogMonitorService
{
    private static readonly IReadOnlyDictionary<string, string> MapPresetToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["TarkovStreets"] = "5714dc692459777137212e12",
        ["Sandbox"] = "653e6760052c01c1c805532f",
        ["Sandbox_high"] = "65b8d6f5cdde2479cb2a3125",
        ["bigmap"] = "56f40101d2720b2a4d8b45d6",
        ["factory4_day"] = "55f2d3fd4bdc2d5f408b4567",
        ["factory4_night"] = "59fc81d786f774390775787e",
        ["Interchange"] = "5714dbc024597771384a510d",
        ["laboratory"] = "5b0fc42d86f7744a585f9105",
        ["Lighthouse"] = "5704e4dad2720bb55b8b4567",
        ["RezervBase"] = "5704e5fad2720bc05b8b4567",
        ["Shoreline"] = "5704e554d2720bac5b8b456e",
        ["Woods"] = "5704e3c2d2720bac5b8b4567"
    };

    private readonly MapMetadataService mapMetadataService;
    private readonly Dictionary<string, LogFileState> fileStates = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, RaidMapInfo> mapLookup = new Dictionary<string, RaidMapInfo>(StringComparer.OrdinalIgnoreCase);
    private RaidLogSnapshot snapshot = RaidLogSnapshot.NoDirectory();
    private string? lastLogDirectory;

    public RaidLogMonitorService()
        : this(new MapMetadataService())
    {
    }

    public RaidLogMonitorService(MapMetadataService mapMetadataService)
    {
        this.mapMetadataService = mapMetadataService;
        RebuildMapLookup();
    }

    public async Task<RaidLogSnapshot> RefreshAsync(string? configuredPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            snapshot = RaidLogSnapshot.NoDirectory();
            lastLogDirectory = null;
            return snapshot;
        }

        try
        {
            var logsRoot = ResolveLogsRoot(configuredPath);
            if (logsRoot is null)
            {
                snapshot = CreateErrorSnapshot(configuredPath, "未找到 Logs 目录");
                return snapshot;
            }

            var logDirectory = ResolveLatestLogDirectory(logsRoot);
            if (logDirectory is null)
            {
                snapshot = CreateWaitingSnapshot(logsRoot, "等待最新 log_* 目录");
                return snapshot;
            }

            if (!string.Equals(lastLogDirectory, logDirectory, StringComparison.OrdinalIgnoreCase))
            {
                snapshot = RaidLogSnapshot.NoDirectory();
                lastLogDirectory = logDirectory;
                fileStates.Clear();
            }

            var files = ResolveLogFiles(logDirectory);
            if (files.Count == 0)
            {
                snapshot = CreateWaitingSnapshot(logDirectory, "等待 application/notifications 日志");
                return snapshot;
            }

            var events = new List<RaidLogEvent>();
            var hasFileChanges = false;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await ReadChangedTextAsync(file, cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                hasFileChanges = true;
                events.AddRange(RaidLogParser.ParseEntries(text)
                    .Select(entry => RaidLogParser.ExtractEvent(entry, ResolveMap))
                    .OfType<RaidLogEvent>());
            }

            var latestFile = files
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();
            if (!hasFileChanges)
            {
                return snapshot;
            }

            snapshot = RaidLogParser.Reduce(
                snapshot,
                events,
                logDirectory,
                latestFile,
                new DateTimeOffset(File.GetLastWriteTime(latestFile)));
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            snapshot = CreateErrorSnapshot(configuredPath, ex.Message);
            return snapshot;
        }
    }

    private async Task<string> ReadChangedTextAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            fileStates.Remove(path);
            return "";
        }

        if (fileStates.TryGetValue(path, out var state) &&
            state.Length == info.Length &&
            state.ModifiedUtc == info.LastWriteTimeUtc)
        {
            return "";
        }

        var startOffset = 0L;
        if (state is not null && info.Length >= state.Length)
        {
            startOffset = state.Length;
        }
        else if (info.Length > MaxInitialLogReadBytes)
        {
            startOffset = info.Length - MaxInitialLogReadBytes;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        if (startOffset > 0)
        {
            stream.Seek(startOffset, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        fileStates[path] = new LogFileState(info.Length, info.LastWriteTimeUtc);
        return text;
    }

    public void ReloadMapLookup()
    {
        RebuildMapLookup();
    }

    private void RebuildMapLookup()
    {
        var lookup = new Dictionary<string, RaidMapInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in mapMetadataService.LoadMaps())
        {
            var info = new RaidMapInfo(map.Id, map.Name);
            AddLookup(lookup, map.Id, info);
            AddLookup(lookup, map.Key, info);
            AddLookup(lookup, map.Name, info);
            AddLookup(lookup, map.NormalizedName, info);
            AddLookup(lookup, map.NameId, info);
        }

        foreach (var pair in MapPresetToId)
        {
            if (lookup.TryGetValue(pair.Value, out var info))
            {
                AddLookup(lookup, pair.Key, info);
            }
            else
            {
                AddLookup(lookup, pair.Key, new RaidMapInfo(pair.Value, pair.Key));
            }
        }

        mapLookup = lookup;
    }

    private RaidMapInfo ResolveMap(string? value)
    {
        var token = NormalizeToken(value);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new RaidMapInfo(null, null);
        }

        return mapLookup.TryGetValue(token, out var map) ? map : new RaidMapInfo(null, value?.Trim());
    }

    private static string? ResolveLogsRoot(string configuredPath)
    {
        var path = configuredPath.Trim();
        if (!Directory.Exists(path))
        {
            return null;
        }

        if (ParseLogDirectoryStamp(Path.GetFileName(path)) is not null)
        {
            return path;
        }

        if (string.Equals(Path.GetFileName(path), "Logs", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var logs = Path.Combine(path, "Logs");
        return Directory.Exists(logs) ? logs : null;
    }

    private static string? ResolveLatestLogDirectory(string logsRoot)
    {
        if (ParseLogDirectoryStamp(Path.GetFileName(logsRoot)) is not null)
        {
            return logsRoot;
        }

        return Directory.EnumerateDirectories(logsRoot)
            .Select(path => new { Path = path, Stamp = ParseLogDirectoryStamp(Path.GetFileName(path)) })
            .Where(item => item.Stamp is not null)
            .OrderBy(item => item.Stamp)
            .LastOrDefault()
            ?.Path;
    }

    private static IReadOnlyList<string> ResolveLogFiles(string logDirectory)
    {
        return Directory.EnumerateFiles(logDirectory)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains("application", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("notifications", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private const int MaxInitialLogReadBytes = 1024 * 1024;

    private static async Task<string> ReadRecentTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        if (stream.Length > MaxInitialLogReadBytes)
        {
            stream.Seek(-MaxInitialLogReadBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static DateTimeOffset? ParseLogDirectoryStamp(string name)
    {
        var match = LogDirectoryRegex().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var stamp = match.Groups["stamp"].Value;
        return DateTimeOffset.TryParseExact(
            stamp,
            "yyyy.MM.dd_H-mm-ss",
            null,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var value)
                ? value
                : null;
    }

    private static RaidLogSnapshot CreateWaitingSnapshot(string logDirectory, string detail)
    {
        return new RaidLogSnapshot(
            RaidLogStatus.Waiting,
            RaidState.Waiting,
            logDirectory,
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
            "等待日志",
            detail,
            null,
            []);
    }

    private static RaidLogSnapshot CreateErrorSnapshot(string configuredPath, string message)
    {
        return new RaidLogSnapshot(
            RaidLogStatus.Error,
            RaidState.Unknown,
            configuredPath,
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
            "日志异常",
            message,
            message,
            []);
    }

    private static void AddLookup(Dictionary<string, RaidMapInfo> lookup, string? value, RaidMapInfo info)
    {
        var token = NormalizeToken(value);
        if (!string.IsNullOrWhiteSpace(token))
        {
            lookup[token] = info;
        }
    }

    private static string NormalizeToken(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? "";
    }

    [GeneratedRegex(@"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{2}-\d{2})_(?<suffix>[0-9.]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogDirectoryRegex();

    private sealed record LogFileState(long Length, DateTime ModifiedUtc);
}
