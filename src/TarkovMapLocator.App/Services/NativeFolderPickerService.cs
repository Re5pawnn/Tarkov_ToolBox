using System.Text.RegularExpressions;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace TarkovMapLocator.App.Services;

public sealed class NativeFolderPickerService
{
    private static readonly Regex LogDirectoryRegex = new(
        @"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{2}-\d{2})_(?<suffix>[0-9.]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Window owner;
    private readonly LocalPathConfigService configService;

    public NativeFolderPickerService(Window owner, LocalPathConfigService configService)
    {
        this.owner = owner;
        this.configService = configService;
    }

    public async Task<string?> PickFolderAsync(string purpose)
    {
        var kind = NormalizePurpose(purpose);
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(folder.Path))
        {
            throw new InvalidOperationException("Selected folder does not expose a filesystem path.");
        }

        if (kind == "game" && ResolveLatestLogDirectory(folder.Path) is null)
        {
            throw new InvalidOperationException("No Logs directory or valid log_* directory was found.");
        }

        configService.SaveLocalPath(kind, folder.Path);
        return folder.Path;
    }

    private static string NormalizePurpose(string purpose)
    {
        return purpose switch
        {
            "screenshots" or "screenshot" => "screenshot",
            "logs" or "game" => "game",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), "Unsupported folder picker purpose.")
        };
    }

    private static string? ResolveLatestLogDirectory(string selectedPath)
    {
        if (IsLogLeafDirectory(selectedPath))
        {
            return selectedPath;
        }

        var logsRoot = ResolveLogsRoot(selectedPath);
        if (logsRoot is null)
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(logsRoot)
                .Where(IsLogDirectoryName)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLogsRoot(string path)
    {
        if (string.Equals(Path.GetFileName(path), "Logs", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
        {
            return path;
        }

        var logsPath = Path.Combine(path, "Logs");
        return Directory.Exists(logsPath) ? logsPath : null;
    }

    private static bool IsLogLeafDirectory(string path)
    {
        if (!Directory.Exists(path) || !IsLogDirectoryName(path))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(path)
                .Any(file =>
                {
                    var name = Path.GetFileName(file);
                    return name.Contains("application", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("notifications", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLogDirectoryName(string path)
    {
        return LogDirectoryRegex.IsMatch(Path.GetFileName(path));
    }
}
