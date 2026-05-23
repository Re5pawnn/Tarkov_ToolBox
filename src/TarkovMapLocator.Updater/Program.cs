using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLocator.Updater;

internal static class Program
{
    private const string ProductName = "TarkovMapLocator";
    private const string MainExeName = "TarkovMapLocator.exe";
    private const string ProcessName = "TarkovMapLocator";
    private const string PayloadDirectoryName = "payload";
    private const string AppStateDirectoryName = "TarkovMapLocator";
    private const string AppSettingsFileName = "app-settings.json";
    private static readonly string[] DeprecatedRootFiles =
    [
        "app.js",
        "index.html",
        "styles.css",
        "start_tool.bat",
        "start_tool_hidden.vbs",
        "WebView2Loader.dll",
        "Microsoft.Web.WebView2.Core.dll",
        "Microsoft.Web.WebView2.WinForms.dll",
        "Microsoft.Web.WebView2.Wpf.dll"
    ];

    private static readonly string[] DeprecatedRootDirectories =
    [
        "TarkovMapLocator.exe.WebView2"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            ShowError($"更新失败：{ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var assumeYes = HasFlag(args, "--yes");
        var launchAfterUpdate = !HasFlag(args, "--no-launch");
        var packageDirectory = AppContext.BaseDirectory;
        var payloadDirectory = Path.Combine(packageDirectory, PayloadDirectoryName);
        if (!Directory.Exists(payloadDirectory) || !File.Exists(Path.Combine(payloadDirectory, MainExeName)))
        {
            ShowError($"没有找到新版程序文件。\n\n请确认更新器旁边存在 {PayloadDirectoryName} 文件夹，且里面包含 {MainExeName}。");
            return 1;
        }

        var targetDirectory = ResolveTargetDirectory(args);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return 2;
        }

        targetDirectory = Path.GetFullPath(targetDirectory);
        if (AreSameDirectory(payloadDirectory, targetDirectory))
        {
            ShowError("新版 payload 目录不能作为旧版程序目录。");
            return 1;
        }

        if (IsAppRunning())
        {
            ShowError("检测到 TarkovMapLocator 仍在运行。\n\n请先关闭旧版工具，再重新运行更新器。");
            return 3;
        }

        var targetExe = Path.Combine(targetDirectory, MainExeName);
        var message =
            $"即将把新版文件覆盖到：\n{targetDirectory}\n\n" +
            "更新器会覆盖新版程序文件，并清理已废弃的旧网页/WebView 文件；不会删除用户 AppData 配置。\n\n" +
            "继续更新吗？";
        if (!assumeYes &&
            MessageBox.Show(message, ProductName + " 更新器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return 2;
        }

        Directory.CreateDirectory(targetDirectory);
        RemoveDeprecatedFiles(targetDirectory);
        CopyDirectory(payloadDirectory, targetDirectory);
        SaveInstallDirectory(targetDirectory);

        if (launchAfterUpdate && File.Exists(targetExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = targetDirectory,
                UseShellExecute = true
            });
        }

        if (!assumeYes)
        {
            var doneText = launchAfterUpdate ? "更新完成，已启动新版工具。" : "更新完成。";
            MessageBox.Show(doneText, ProductName + " 更新器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Any(item => string.Equals(item, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveTargetDirectory(string[] args)
    {
        var targetFromArgs = ReadTargetArgument(args);
        if (IsValidTargetDirectory(targetFromArgs))
        {
            return targetFromArgs;
        }

        var savedInstallDirectory = ReadSavedInstallDirectory();
        if (IsValidTargetDirectory(savedInstallDirectory))
        {
            return savedInstallDirectory;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择旧版 TarkovMapLocator.exe 所在文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        while (dialog.ShowDialog() == DialogResult.OK)
        {
            if (IsValidTargetDirectory(dialog.SelectedPath))
            {
                return dialog.SelectedPath;
            }

            MessageBox.Show(
                $"所选目录里没有找到 {MainExeName}。\n\n请重新选择旧版工具所在文件夹。",
                ProductName + " 更新器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        return null;
    }

    private static string? ReadTargetArgument(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var item = args[index];
            if (string.Equals(item, "--target", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }

            const string prefix = "--target=";
            if (item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return item[prefix.Length..];
            }
        }

        return null;
    }

    private static bool IsValidTargetDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(Path.GetFullPath(value), MainExeName));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAppRunning()
    {
        try
        {
            return Process.GetProcessesByName(ProcessName).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            var targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            File.Copy(file, targetPath, overwrite: true);
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
    }

    private static void RemoveDeprecatedFiles(string targetDirectory)
    {
        foreach (var fileName in DeprecatedRootFiles)
        {
            TryDeleteFile(Path.Combine(targetDirectory, fileName));
        }

        foreach (var file in Directory.EnumerateFiles(targetDirectory, "Microsoft.Web.WebView2*.dll", SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(file);
        }

        foreach (var directoryName in DeprecatedRootDirectories)
        {
            TryDeleteDirectory(Path.Combine(targetDirectory, directoryName));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string? ReadSavedInstallDirectory()
    {
        try
        {
            var settings = ReadSettings();
            return settings["installDirectory"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static void SaveInstallDirectory(string installDirectory)
    {
        try
        {
            var settings = ReadSettings();
            settings["schemaVersion"] = 1;
            settings["installDirectory"] = Path.GetFullPath(installDirectory);
            settings["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            WriteSettings(settings);
        }
        catch
        {
        }
    }

    private static JsonObject ReadSettings()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(GetSettingsPath())) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteSettings(JsonObject settings)
    {
        var path = GetSettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(
            tempPath,
            settings.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppStateDirectoryName,
            AppSettingsFileName);
    }

    private static bool AreSameDirectory(string first, string second)
    {
        return string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(message, ProductName + " 更新器", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
