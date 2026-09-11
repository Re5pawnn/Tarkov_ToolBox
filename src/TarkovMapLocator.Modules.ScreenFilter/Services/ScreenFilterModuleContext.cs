using TarkovMapLocator.ModuleContracts;
using System.IO;

namespace TarkovMapLocator.Modules.ScreenFilter.Services;

internal static class ScreenFilterModuleContext
{
    private static Action<FeatureLogLevel, string, string, string?>? _writer;

    public static string DataDirectory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocatorDesktop");

    public static void Initialize(IFeatureHost host)
    {
        DataDirectory = host.DataDirectory;
        _writer = host.WriteLog;
    }

    public static void Initialize(FeatureApplicationContext context)
    {
        DataDirectory = context.DataDirectory;
        _writer = context.WriteLog;
    }

    public static void Write(FeatureLogLevel level, string category, string message, string? details = null) =>
        _writer?.Invoke(level, category, message, details);
}

internal static class ApplicationIdentity
{
    public static string ApplicationDataDirectory => ScreenFilterModuleContext.DataDirectory;
}

internal static class RuntimeLogService
{
    public static void Info(string category, string message, string? details = null) =>
        ScreenFilterModuleContext.Write(FeatureLogLevel.Info, category, message, details);

    public static void Warning(string category, string message, string? details = null) =>
        ScreenFilterModuleContext.Write(FeatureLogLevel.Warning, category, message, details);

    public static void Error(string category, string message, Exception exception, string? details = null) =>
        ScreenFilterModuleContext.Write(
            FeatureLogLevel.Error,
            category,
            message,
            string.IsNullOrWhiteSpace(details) ? exception.ToString() : $"{details}\n{exception}");
}
