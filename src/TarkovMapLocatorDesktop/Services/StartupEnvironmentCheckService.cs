using System.IO;
using System.Runtime.InteropServices;

namespace TarkovMapLocatorDesktop.Services;

internal sealed record StartupEnvironmentCheckResult(
    bool IsReady,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> LoadedRuntimeFiles)
{
    public string ToLogText() =>
        $"原生运行库: {(IsReady ? "完整" : "异常")}\n" +
        $"已验证: {(LoadedRuntimeFiles.Count == 0 ? "无" : string.Join("、", LoadedRuntimeFiles))}" +
        (Problems.Count == 0 ? string.Empty : $"\n问题: {string.Join("；", Problems)}");
}
internal static class StartupEnvironmentCheckService
{
    internal static readonly string[] RequiredAppLocalVcRuntimeFiles =
    [
        "msvcp140.dll",
        "msvcp140_1.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll"
    ];

    public static StartupEnvironmentCheckResult CheckAppLocalVcRuntime()
    {
        var problems = new List<string>();
        var loaded = new List<string>();

        foreach (var fileName in RequiredAppLocalVcRuntimeFiles)
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                problems.Add($"程序目录缺少 {fileName}");
                continue;
            }

            nint handle = 0;
            try
            {
                handle = NativeLibrary.Load(path);
                loaded.Add(fileName);
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or FileLoadException)
            {
                problems.Add($"{fileName} 无法加载（{exception.GetBaseException().Message}）");
            }
            finally
            {
                if (handle != 0) NativeLibrary.Free(handle);
            }
        }

        return new StartupEnvironmentCheckResult(problems.Count == 0, problems, loaded);
    }
}
