using System.IO;
using System.Text;
using System.Text.Json;
using TarkovMapLocator.Modules.TaskTracking;
using TarkovMapLocator.Modules.TaskTracking.Models;

namespace TarkovMapLocator.Modules.TaskTracking.Services;

public static class TaskTrackingProgressService
{
    private const int SchemaVersion = 2;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string ProgressPath => Path.Combine(TaskTrackingRuntime.DataDirectory, "task-progress.json");

    public static Dictionary<string, Dictionary<string, TaskTrackingStatus>> LoadByMode()
    {
        lock (Gate)
        {
            return LoadByModeCore(ProgressPath);
        }
    }

    internal static Dictionary<string, Dictionary<string, TaskTrackingStatus>> LoadByModeForTest(string path)
    {
        lock (Gate) return LoadByModeCore(path);
    }

    public static bool SaveByMode(
        IReadOnlyDictionary<string, Dictionary<string, TaskTrackingStatus>> statusesByMode)
    {
        lock (Gate)
        {
            return SaveByModeCore(ProgressPath, statusesByMode);
        }
    }

    internal static bool SaveByModeForTest(
        string path,
        IReadOnlyDictionary<string, Dictionary<string, TaskTrackingStatus>> statusesByMode)
    {
        lock (Gate) return SaveByModeCore(path, statusesByMode);
    }

    private static Dictionary<string, Dictionary<string, TaskTrackingStatus>> LoadByModeCore(string progressPath)
    {
        var candidates = new[] { progressPath, BackupPath(progressPath, 1), BackupPath(progressPath, 2) };
        Exception? primaryFailure = null;
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            if (!File.Exists(candidate)) continue;
            if (TryLoad(candidate, out var modes, out var failure))
            {
                if (index > 0)
                {
                    RestoreRecoveredProgress(progressPath, candidate);
                    TaskTrackingRuntime.Warning(
                        "任务进度已从备份自动恢复",
                        $"备份: {Path.GetFileName(candidate)}\n原文件错误: {primaryFailure?.Message ?? "文件缺失"}");
                }
                return modes;
            }

            if (index == 0) primaryFailure = failure;
        }

        if (candidates.Any(File.Exists))
            TaskTrackingRuntime.Warning("任务进度及备份均无法读取，已使用空进度", primaryFailure?.ToString());
        return EmptyModes();
    }

    private static bool SaveByModeCore(
        string progressPath,
        IReadOnlyDictionary<string, Dictionary<string, TaskTrackingStatus>> statusesByMode)
    {
        string? temporaryPath = null;
        try
        {
            var normalized = NormalizeModes(statusesByMode);
            Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
            temporaryPath = $"{progressPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            WriteDurableText(temporaryPath, JsonSerializer.Serialize(new ProgressFile(SchemaVersion, normalized), JsonOptions));

            // Only rotate a valid primary. A damaged primary must never evict the
            // last known-good backups before the newly serialized file is installed.
            if (File.Exists(progressPath) && TryLoad(progressPath, out _, out _))
            {
                var firstBackup = BackupPath(progressPath, 1);
                var secondBackup = BackupPath(progressPath, 2);
                if (File.Exists(firstBackup)) File.Copy(firstBackup, secondBackup, overwrite: true);
                File.Copy(progressPath, firstBackup, overwrite: true);
            }

            File.Move(temporaryPath, progressPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TaskTrackingRuntime.Warning("保存任务进度失败", exception.ToString());
            return false;
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
        }
    }

    private static bool TryLoad(
        string path,
        out Dictionary<string, Dictionary<string, TaskTrackingStatus>> modes,
        out Exception? failure)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var version = document.RootElement.TryGetProperty("schemaVersion", out var schema)
                ? schema.GetInt32()
                : 0;
            if (version == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyProgressFile>(json, JsonOptions);
                modes = PartitionLegacyStatuses(legacy?.Statuses ?? new(StringComparer.Ordinal));
            }
            else if (version == SchemaVersion)
            {
                var file = JsonSerializer.Deserialize<ProgressFile>(json, JsonOptions);
                modes = NormalizeModes(file?.Modes);
            }
            else
            {
                throw new JsonException($"不支持的任务进度版本: {version}");
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            modes = EmptyModes();
            failure = exception;
            return false;
        }
    }

    private static void RestoreRecoveredProgress(string progressPath, string backupPath)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
            if (File.Exists(progressPath))
            {
                var corruptPath = $"{progressPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmssfff}";
                File.Move(progressPath, corruptPath, overwrite: false);
            }

            temporaryPath = $"{progressPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.restore.tmp";
            File.Copy(backupPath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, progressPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TaskTrackingRuntime.Warning("已读取任务进度备份，但恢复原文件失败", exception.Message);
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
        }
    }

    private static void WriteDurableText(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            writer.Write(content);
            writer.Flush();
        }
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteTemporary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TaskTrackingRuntime.Warning("清理任务进度临时文件失败", exception.Message);
        }
    }

    private static string BackupPath(string progressPath, int generation) => $"{progressPath}.{generation}.bak";

    internal static Dictionary<string, Dictionary<string, TaskTrackingStatus>> PartitionLegacyStatuses(
        IReadOnlyDictionary<string, TaskTrackingStatus> statuses)
    {
        var result = EmptyModes();
        foreach (var pair in statuses)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is not (TaskTrackingStatus.Accepted or TaskTrackingStatus.Completed))
                continue;
            var mode = pair.Key.StartsWith("pvp:", StringComparison.Ordinal)
                ? "pvp"
                : pair.Key.StartsWith("season:", StringComparison.Ordinal)
                    ? "season"
                    : "pve";
            result[mode][pair.Key] = pair.Value;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, TaskTrackingStatus>> NormalizeModes(
        IReadOnlyDictionary<string, Dictionary<string, TaskTrackingStatus>>? modes)
    {
        var result = EmptyModes();
        if (modes is null) return result;
        foreach (var mode in result.Keys.ToArray())
        {
            if (!modes.TryGetValue(mode, out var statuses) || statuses is null) continue;
            result[mode] = statuses
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                               pair.Value is TaskTrackingStatus.Accepted or TaskTrackingStatus.Completed)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, TaskTrackingStatus>> EmptyModes() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pvp"] = new(StringComparer.Ordinal),
            ["pve"] = new(StringComparer.Ordinal),
            ["season"] = new(StringComparer.Ordinal)
        };

    private sealed record LegacyProgressFile(int SchemaVersion, Dictionary<string, TaskTrackingStatus> Statuses);
    private sealed record ProgressFile(
        int SchemaVersion,
        Dictionary<string, Dictionary<string, TaskTrackingStatus>> Modes);
}
