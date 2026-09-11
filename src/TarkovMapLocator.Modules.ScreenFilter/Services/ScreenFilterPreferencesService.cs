using System.IO;
using System.Text.Json;
using System.Threading;
using TarkovMapLocator.Modules.ScreenFilter.Models;

namespace TarkovMapLocator.Modules.ScreenFilter.Services;

/// <summary>
/// Keeps screen-filter choices in a dedicated desktop-only file, mirroring the
/// original application's separate screen-filter.json without touching it.
/// </summary>
public static class ScreenFilterPreferencesService
{
    private static readonly object SaveQueueGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string PreferencePath = Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "screen-filter.json");
    private static Task _queuedSave = Task.CompletedTask;

    public static ScreenFilterRequest Load()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return ScreenFilterRequest.Default;
            return ParseOrDefault(File.ReadAllText(PreferencePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("滤镜", "读取滤镜配置失败，已使用默认值", exception.ToString());
            return ScreenFilterRequest.Default;
        }
    }

    internal static ScreenFilterRequest ParseForTest(string? json) => ParseOrDefault(json);

    private static ScreenFilterRequest ParseOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ScreenFilterRequest.Default;
        try
        {
            return JsonSerializer.Deserialize<ScreenFilterRequest>(json, JsonOptions)?.Normalize()
                ?? ScreenFilterRequest.Default;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            RuntimeLogService.Warning("滤镜", "滤镜配置格式无效，已使用默认值", exception.Message);
            return ScreenFilterRequest.Default;
        }
    }

    public static bool Save(ScreenFilterRequest request)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(PreferencePath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = $"{PreferencePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(request.Normalize(), JsonOptions));
            File.Move(temporaryPath, PreferencePath, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            // A persisted preset is convenient, but never required for filter use.
            RuntimeLogService.Warning("滤镜", "保存滤镜配置失败，本次调整仅在当前会话生效", exception.ToString());
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogService.Warning("滤镜", "清理滤镜配置临时文件失败", exception.Message);
                }
            }
        }
    }

    /// <summary>
    /// Serializes debounced UI writes in their original order. The returned task can
    /// be awaited during shutdown so the last slider adjustment is never lost.
    /// </summary>
    public static Task QueueSaveAsync(ScreenFilterRequest request)
    {
        var snapshot = request.Normalize();
        lock (SaveQueueGate)
        {
            _queuedSave = _queuedSave.ContinueWith(
                _ => Save(snapshot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return _queuedSave;
        }
    }

    public static Task FlushAsync()
    {
        lock (SaveQueueGate) return _queuedSave;
    }
}
