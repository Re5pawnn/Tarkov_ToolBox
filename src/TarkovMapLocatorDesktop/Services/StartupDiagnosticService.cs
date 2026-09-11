using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// A synchronous, minimal startup journal that remains useful when WPF or the
/// asynchronous runtime logger cannot finish initializing.
/// </summary>
internal static class StartupDiagnosticService
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocatorDesktop",
        "logs");

    public static string LogFilePath { get; } = Path.Combine(DirectoryPath, "startup-latest.log");

    public static void Begin()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(
                    LogFilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] 进程入口{Environment.NewLine}" +
                    $"版本: {typeof(StartupDiagnosticService).Assembly.GetName().Version}{Environment.NewLine}" +
                    $"程序目录: {AppContext.BaseDirectory}{Environment.NewLine}" +
                    $"系统: {RuntimeInformation.OSDescription}{Environment.NewLine}" +
                    $"架构: {RuntimeInformation.ProcessArchitecture}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never become a startup dependency.
            }
        }
    }

    public static void Mark(string stage, string? detail = null)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {stage}";
                if (!string.IsNullOrWhiteSpace(detail)) line += $": {detail}";
                File.AppendAllText(LogFilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    public static void Fail(string stage, Exception exception) =>
        Mark($"失败/{stage}", exception.ToString());
}
