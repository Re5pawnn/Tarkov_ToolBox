using System.IO;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.TaskTracking;

internal static class TaskTrackingRuntime
{
    private static IFeatureHost? _host;

    internal static string DataDirectory => _host?.DataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocatorDesktop");

    internal static string ModuleDirectory
    {
        get
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(TaskTrackingRuntime).Assembly.Location)
                                    ?? AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(assemblyDirectory, "task-data", "task-tracking-catalog.json")))
                return assemblyDirectory;
            var packagedDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "TaskTracking");
            return File.Exists(Path.Combine(packagedDirectory, "task-data", "task-tracking-catalog.json"))
                ? packagedDirectory
                : assemblyDirectory;
        }
    }

    internal static void Attach(IFeatureHost host) => _host = host;

    internal static void Warning(string message, string? details = null) =>
        _host?.WriteLog(FeatureLogLevel.Warning, "任务追踪", message, details);
}
