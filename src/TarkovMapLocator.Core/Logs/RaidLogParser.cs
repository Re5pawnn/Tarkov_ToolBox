using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovMapLocator.Core.Logs;

public sealed record RaidMapInfo(string? MapId, string? MapName);

public static partial class RaidLogParser
{
    private const int MaxRecentEvents = 5;

    public static IReadOnlyList<RaidLogEntry> ParseEntries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var entries = new List<RaidLogEntry>();
        var current = "";
        foreach (var line in text.Split('\n'))
        {
            var normalizedLine = line.TrimEnd('\r');
            if (LogEntryStartRegex().IsMatch(normalizedLine))
            {
                AddEntry(entries, current);
                current = normalizedLine;
            }
            else if (!string.IsNullOrEmpty(current))
            {
                current += "\n" + normalizedLine;
            }
        }

        AddEntry(entries, current);
        return entries;
    }

    public static RaidLogEvent? ExtractEvent(RaidLogEntry entry, Func<string?, RaidMapInfo> resolveMap)
    {
        var sessionMode = ParseSessionMode(entry.Message);
        if (!string.IsNullOrWhiteSpace(sessionMode))
        {
            return new RaidLogEvent("session_mode", entry.Timestamp, "会话模式", sessionMode, SessionMode: sessionMode);
        }

        var raidLine = ParseRaidLine(entry, resolveMap);
        if (raidLine is not null)
        {
            return raidLine;
        }

        var networkGame = ParseNetworkGameCreate(entry, resolveMap);
        if (networkGame is not null)
        {
            return networkGame;
        }

        var queue = ParseDuration(entry.Message, "MatchingCompleted");
        if (queue is not null)
        {
            return new RaidLogEvent(
                "matching_completed",
                entry.Timestamp,
                "匹配完成",
                $"排队耗时 {queue:0.0}s",
                QueueDurationSeconds: queue);
        }

        var mapLoading = ParseMapLoading(entry, resolveMap);
        if (mapLoading is not null)
        {
            return mapLoading;
        }

        if (Regex.IsMatch(entry.Message, @"application\|GameStarting", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new RaidLogEvent("game_starting", entry.Timestamp, "进入战局中", "客户端正在切换到战局");
        }

        var load = ParseDuration(entry.Message, "LocationLoaded");
        if (load is not null)
        {
            return new RaidLogEvent(
                "location_loaded",
                entry.Timestamp,
                "地图载入完成",
                $"加载耗时 {load:0.0}s",
                LoadDurationSeconds: load);
        }

        if (Regex.IsMatch(entry.Message, @"application\|GameStarted", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new RaidLogEvent("game_started", entry.Timestamp, "已进入战局", "战局开始");
        }

        if (Regex.IsMatch(entry.Message, @"application\|Network game matching (aborted|cancelled)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new RaidLogEvent("matching_aborted", entry.Timestamp, "匹配已取消", "本次匹配已中断");
        }

        var matchOver = ParseUserMatchOver(entry, resolveMap);
        if (matchOver is not null)
        {
            return matchOver;
        }

        return null;
    }

    public static RaidLogSnapshot Reduce(
        RaidLogSnapshot previous,
        IReadOnlyList<RaidLogEvent> events,
        string logDirectory,
        string latestLogFile,
        DateTimeOffset latestLogModifiedAt)
    {
        var state = previous.LogStatus == RaidLogStatus.Error ? RaidState.Unknown : previous.RaidState;
        var mapId = previous.MapId;
        var mapName = previous.MapName;
        var location = previous.Location;
        var raidId = previous.RaidId;
        var sessionMode = previous.SessionMode;
        var serverIp = previous.ServerIp;
        var serverPort = previous.ServerPort;
        var queueDuration = previous.QueueDurationSeconds;
        var loadDuration = previous.LoadDurationSeconds;
        var gameStartAt = previous.GameStartAt;
        var raidEndAt = previous.RaidEndAt;
        var lastTitle = previous.LastEventTitle;
        var lastDetail = previous.LastEventDetail;
        var recentEvents = previous.RecentEvents.Concat(events).OrderBy(item => item.Timestamp).ToList();

        foreach (var item in recentEvents)
        {
            lastTitle = item.Title;
            lastDetail = item.Detail;
            sessionMode = item.SessionMode ?? sessionMode;
            mapId = item.MapId ?? mapId;
            mapName = item.MapName ?? mapName;
            location = item.Location ?? location;
            raidId = item.RaidId ?? raidId;
            serverIp = item.ServerIp ?? serverIp;
            serverPort = item.ServerPort ?? serverPort;
            queueDuration = item.QueueDurationSeconds ?? queueDuration;
            loadDuration = item.LoadDurationSeconds ?? loadDuration;

            state = item.Type switch
            {
                "raid_created" or "network_game_create" => RaidState.Matching,
                "matching_completed" or "map_loading" or "game_starting" or "location_loaded" => RaidState.Loading,
                "game_started" => RaidState.InRaid,
                "user_match_over" => RaidState.Ended,
                "matching_aborted" => RaidState.Aborted,
                _ => state
            };

            if (item.Type == "game_started")
            {
                gameStartAt = item.Timestamp;
                raidEndAt = null;
            }
            else if (item.Type is "user_match_over" or "matching_aborted")
            {
                raidEndAt = item.Timestamp;
            }
        }

        recentEvents = recentEvents
            .GroupBy(item => $"{item.Type}:{item.Timestamp.ToUnixTimeMilliseconds()}:{item.Detail}", StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderByDescending(item => item.Timestamp)
            .Take(MaxRecentEvents)
            .OrderBy(item => item.Timestamp)
            .ToList();

        if (state == RaidState.Unknown && File.Exists(latestLogFile))
        {
            state = RaidState.Waiting;
        }

        return new RaidLogSnapshot(
            RaidLogStatus.Ready,
            state,
            logDirectory,
            latestLogFile,
            latestLogModifiedAt,
            DateTimeOffset.Now,
            mapId,
            mapName,
            location,
            raidId,
            sessionMode,
            serverIp,
            serverPort,
            queueDuration,
            loadDuration,
            gameStartAt,
            raidEndAt,
            lastTitle,
            lastDetail,
            null,
            recentEvents);
    }

    private static void AddEntry(List<RaidLogEntry> entries, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = LogEntryRegex().Match(text.Trim());
        if (!match.Success)
        {
            return;
        }

        var timestamp = ParseTimestamp(
            match.Groups["date"].Value,
            match.Groups["time"].Value,
            match.Groups["tzoffset"].Value);
        var tail = string.Join('\n', text.Split('\n').Skip(1)).Trim();
        entries.Add(new RaidLogEntry(timestamp, match.Groups["message"].Value.Trim(), tail));
    }

    private static DateTimeOffset ParseTimestamp(string date, string time, string offset)
    {
        var text = string.IsNullOrWhiteSpace(offset)
            ? $"{date}T{time}"
            : $"{date}T{time}{offset.Trim()}";
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var value)
                ? value
                : DateTimeOffset.Now;
    }

    private static string? ParseSessionMode(string message)
    {
        var match = Regex.Match(message, @"Session mode: (?<mode>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var mode = match.Groups["mode"].Value.Trim().ToUpperInvariant();
        return mode == "REGULAR" ? "Regular" : mode;
    }

    private static RaidLogEvent? ParseRaidLine(RaidLogEntry entry, Func<string?, RaidMapInfo> resolveMap)
    {
        var match = Regex.Match(
            entry.Message,
            @"'Profileid: (.+?), Status: (.+?), RaidMode: (.+?), Ip: (.+?), Port: (.+?), Location: (.+?), Sid: (.+?), GameMode: (.+?), shortId: (.+?)'",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups.Count < 10)
        {
            return null;
        }

        var location = match.Groups[6].Value.Trim();
        var map = resolveMap(location);
        return new RaidLogEvent(
            "raid_created",
            entry.Timestamp,
            "战局已创建",
            $"{map.MapName ?? location} / {match.Groups[4].Value.Trim()}",
            map.MapId,
            map.MapName,
            location,
            match.Groups[9].Value.Trim(),
            ServerIp: match.Groups[4].Value.Trim(),
            ServerPort: match.Groups[5].Value.Trim());
    }

    private static RaidLogEvent? ParseNetworkGameCreate(RaidLogEntry entry, Func<string?, RaidMapInfo> resolveMap)
    {
        if (!entry.Message.Contains("application|TRACE-NetworkGameCreate profileStatus", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mapMatch = Regex.Match(entry.Message, @"Location: (?<map>[^,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var raidIdMatch = Regex.Match(entry.Message, @"shortId: (?<raidId>[A-Z0-9]{6})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!mapMatch.Success || !raidIdMatch.Success)
        {
            return null;
        }

        var location = mapMatch.Groups["map"].Value.Trim();
        var map = resolveMap(location);
        return new RaidLogEvent(
            "network_game_create",
            entry.Timestamp,
            "网络战局已创建",
            $"{map.MapName ?? location} / Raid {raidIdMatch.Groups["raidId"].Value}",
            map.MapId,
            map.MapName,
            location,
            raidIdMatch.Groups["raidId"].Value);
    }

    private static RaidLogEvent? ParseMapLoading(RaidLogEntry entry, Func<string?, RaidMapInfo> resolveMap)
    {
        var match = Regex.Match(entry.Message, @"scene preset path:maps/(?<mapBundleName>[a-zA-Z0-9_]+)\.bundle", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var location = match.Groups["mapBundleName"].Value.Trim();
        var map = resolveMap(location);
        return new RaidLogEvent(
            "map_loading",
            entry.Timestamp,
            "开始载入地图",
            map.MapName ?? location,
            map.MapId,
            map.MapName,
            location);
    }

    private static RaidLogEvent? ParseUserMatchOver(RaidLogEntry entry, Func<string?, RaidMapInfo> resolveMap)
    {
        if (!entry.Message.Contains("Got notification | UserMatchOver", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.Json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(entry.Json);
            var root = document.RootElement;
            var location = ReadString(root, "location");
            var raidId = ReadString(root, "shortId");
            var map = resolveMap(location);
            return new RaidLogEvent(
                "user_match_over",
                entry.Timestamp,
                "战局结束",
                string.IsNullOrWhiteSpace(raidId) ? "收到战局结束通知" : $"Raid {raidId}",
                map.MapId,
                map.MapName,
                location,
                raidId);
        }
        catch
        {
            return null;
        }
    }

    private static double? ParseDuration(string message, string marker)
    {
        var match = Regex.Match(message, $@"{Regex.Escape(marker)}:[0-9.,]+ real:(?<value>[0-9.,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(
            match.Groups["value"].Value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var node) &&
            node.ValueKind == JsonValueKind.String
                ? node.GetString()?.Trim()
                : null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}(?: ?[+-]\d{2}:\d{2})?\|")]
    private static partial Regex LogEntryStartRegex();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?<tzoffset> ?[+-]\d{2}:\d{2})?\|(?<message>.+?)$", RegexOptions.Singleline)]
    private static partial Regex LogEntryRegex();
}
