using System.Text.RegularExpressions;

namespace TarkovMapLocator.App.Services;

public sealed class NativePathAutoDetectService
{
    private static readonly Regex SteamLibraryPathRegex = new(
        "\"path\"\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LogDirectoryRegex = new(
        @"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{2}-\d{2})_(?<suffix>[0-9.]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string? DetectScreenshotDirectory()
    {
        return BuildScreenshotCandidates()
            .Where(IsUsableDirectory)
            .OrderByDescending(GetDirectoryActivityUtc)
            .FirstOrDefault();
    }

    public string? DetectLogDirectory()
    {
        return BuildLogCandidates()
            .Select(NormalizeLogsRoot)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsUsableLogRoot)
            .OrderByDescending(GetLogRootActivityUtc)
            .FirstOrDefault();
    }

    public bool IsUsableScreenshotDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && IsUsableDirectory(path);
    }

    public bool IsUsableLogDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            NormalizeLogsRoot(path) is { } logsRoot &&
            IsUsableLogRoot(logsRoot);
    }

    private static IEnumerable<string> BuildScreenshotCandidates()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(documents) && !IsOneDrivePath(documents))
        {
            candidates.Add(Path.Combine(documents, "Escape from Tarkov", "Screenshots"));
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Documents", "Escape from Tarkov", "Screenshots"));
        }

        if (!string.IsNullOrWhiteSpace(pictures) && !IsOneDrivePath(pictures))
        {
            candidates.Add(Path.Combine(pictures, "Escape from Tarkov", "Screenshots"));
            candidates.Add(Path.Combine(pictures, "Escape from Tarkov"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsCloudOrRedirectedPath(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> BuildLogCandidates()
    {
        foreach (var steamLibrary in ReadSteamLibraries())
        {
            yield return Path.Combine(steamLibrary, "steamapps", "common", "Escape from Tarkov", "build", "Logs");
            yield return Path.Combine(steamLibrary, "steamapps", "common", "EscapeFromTarkov", "build", "Logs");
        }

        foreach (var root in DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.RootDirectory.FullName))
        {
            yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", "Escape from Tarkov", "build", "Logs");
            yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", "EscapeFromTarkov", "build", "Logs");
            yield return Path.Combine(root, "Battlestate Games", "Escape from Tarkov", "build", "Logs");
            yield return Path.Combine(root, "Battlestate Games", "EscapeFromTarkov", "build", "Logs");
            yield return Path.Combine(root, "Battlestate Games", "EFT", "build", "Logs");
        }
    }

    private static IEnumerable<string> ReadSteamLibraries()
    {
        var candidates = new List<string>();
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "Steam"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Steam"));
        }

        foreach (var steamRoot in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(steamRoot))
            {
                yield return steamRoot;
            }

            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(libraryFile);
            }
            catch
            {
                continue;
            }

            foreach (Match match in SteamLibraryPathRegex.Matches(text))
            {
                var path = match.Groups["path"].Value.Replace(@"\\", @"\").Trim();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static bool IsUsableDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeLogsRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (!Directory.Exists(trimmed))
        {
            return null;
        }

        if (string.Equals(Path.GetFileName(trimmed), "Logs", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (IsLogDirectoryName(trimmed))
        {
            return Directory.GetParent(trimmed)?.FullName;
        }

        var logsPath = Path.Combine(trimmed, "Logs");
        return Directory.Exists(logsPath) ? logsPath : null;
    }

    private static bool IsUsableLogRoot(string path)
    {
        try
        {
            return Directory.Exists(path) &&
                Directory.EnumerateDirectories(path).Any(IsLogDirectoryName);
        }
        catch
        {
            return false;
        }
    }

    private static DateTime GetDirectoryActivityUtc(string path)
    {
        try
        {
            var newestImage = Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => IsScreenshotFile(file))
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            var directoryTime = Directory.GetLastWriteTimeUtc(path);
            return newestImage > directoryTime ? newestImage : directoryTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime GetLogRootActivityUtc(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Where(IsLogDirectoryName)
                .Select(Directory.GetLastWriteTimeUtc)
                .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(path))
                .Max();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool IsScreenshotFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLogDirectoryName(string path)
    {
        return LogDirectoryRegex.IsMatch(Path.GetFileName(path));
    }

    private static bool IsOneDrivePath(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCloudOrRedirectedPath(string path)
    {
        return IsOneDrivePath(path) || HasReparsePointInExistingPath(path);
    }

    private static bool HasReparsePointInExistingPath(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            while (directory is not null)
            {
                if (directory.Exists &&
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                directory = directory.Parent;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }
}
