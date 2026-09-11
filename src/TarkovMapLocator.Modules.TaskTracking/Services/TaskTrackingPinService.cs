using System.IO;
using System.Text.Json;
using TarkovMapLocator.Modules.TaskTracking;

namespace TarkovMapLocator.Modules.TaskTracking.Services;

public static class TaskTrackingPinService
{
    private const int SchemaVersion = 1;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string StatePath => Path.Combine(TaskTrackingRuntime.DataDirectory, "task-pins.json");

    public static TaskTrackingPinState Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(StatePath)) return TaskTrackingPinState.Empty;
                var state = JsonSerializer.Deserialize<TaskTrackingPinState>(File.ReadAllText(StatePath), JsonOptions);
                return state?.SchemaVersion == SchemaVersion ? state.Normalize() : TaskTrackingPinState.Empty;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                TaskTrackingRuntime.Warning("读取任务标记失败，已使用空标记", exception.ToString());
                return TaskTrackingPinState.Empty;
            }
        }
    }

    public static bool Save(
        double? left,
        double? top,
        double? width,
        double? height,
        bool linkMapTaskPoints)
    {
        lock (Gate)
        {
            string? temporaryPath = null;
            try
            {
                var state = new TaskTrackingPinState(
                    SchemaVersion,
                    NormalizeCoordinate(left),
                    NormalizeCoordinate(top),
                    NormalizeDimension(width, 260, 1200),
                    NormalizeDimension(height, 140, 1600),
                    linkMapTaskPoints);
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                temporaryPath = $"{StatePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporaryPath, StatePath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TaskTrackingRuntime.Warning("保存任务标记失败", exception.ToString());
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath)) try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TaskTrackingRuntime.Warning("清理任务标记临时文件失败", exception.Message);
                }
            }
        }
    }

    private static double? NormalizeCoordinate(double? value) =>
        value is { } coordinate && double.IsFinite(coordinate) && coordinate is >= -100_000 and <= 100_000
            ? coordinate
            : null;

    private static double? NormalizeDimension(double? value, double minimum, double maximum) =>
        value is { } dimension && double.IsFinite(dimension) && dimension >= minimum
            ? Math.Clamp(dimension, minimum, maximum)
            : null;
}

public sealed record TaskTrackingPinState(
    int SchemaVersion,
    double? Left,
    double? Top,
    double? Width = null,
    double? Height = null,
    bool LinkMapTaskPoints = false)
{
    public static TaskTrackingPinState Empty { get; } = new(1, null, null, null, null, false);

    public TaskTrackingPinState Normalize() => new(
        1,
        double.IsFinite(Left ?? double.NaN) ? Left : null,
        double.IsFinite(Top ?? double.NaN) ? Top : null,
        Width is { } width && double.IsFinite(width) && width >= 260 ? Math.Clamp(width, 260, 1200) : null,
        Height is { } height && double.IsFinite(height) && height >= 140 ? Math.Clamp(height, 140, 1600) : null,
        LinkMapTaskPoints);
}
