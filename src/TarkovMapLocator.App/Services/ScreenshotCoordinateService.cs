using System.Text.Json;
using TarkovMapLocator.Core.Maps;

namespace TarkovMapLocator.App.Services;

public sealed class ScreenshotCoordinateService
{
    private const string AppStateDirectoryName = "TarkovMapLocator";
    private const string LocalPathsFileName = "local-paths.json";

    private readonly string localPathsPath;
    private DateTime lastLocalPathsModifiedUtc = DateTime.MinValue;
    private string? cachedScreenshotDirectory;
    private string? lastScannedDirectory;
    private DateTime lastDirectoryModifiedUtc = DateTime.MinValue;
    private ScreenshotCoordinate? cachedLatestCoordinate;

    public ScreenshotCoordinateService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppStateDirectoryName,
            LocalPathsFileName))
    {
    }

    public ScreenshotCoordinateService(string localPathsPath)
    {
        this.localPathsPath = localPathsPath;
    }

    public ScreenshotCoordinate? GetLatestCoordinate()
    {
        var screenshotDirectory = ReadScreenshotDirectory();
        if (string.IsNullOrWhiteSpace(screenshotDirectory) || !Directory.Exists(screenshotDirectory))
        {
            lastScannedDirectory = null;
            lastDirectoryModifiedUtc = DateTime.MinValue;
            cachedLatestCoordinate = null;
            return null;
        }

        try
        {
            var directoryModifiedUtc = Directory.GetLastWriteTimeUtc(screenshotDirectory);
            if (cachedLatestCoordinate is not null &&
                string.Equals(lastScannedDirectory, screenshotDirectory, StringComparison.OrdinalIgnoreCase) &&
                directoryModifiedUtc == lastDirectoryModifiedUtc)
            {
                return cachedLatestCoordinate;
            }

            ScreenshotCoordinate? best = null;
            foreach (var path in Directory.EnumerateFiles(screenshotDirectory, "*.png", SearchOption.TopDirectoryOnly))
            {
                var candidate = ParseScreenshotFile(path);
                if (candidate is null)
                {
                    continue;
                }

                if (best is null || CompareCoordinate(candidate, best) > 0)
                {
                    best = candidate;
                }
            }

            lastScannedDirectory = screenshotDirectory;
            lastDirectoryModifiedUtc = directoryModifiedUtc;
            cachedLatestCoordinate = best;
            return best;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? ReadScreenshotDirectory()
    {
        try
        {
            var modifiedUtc = File.GetLastWriteTimeUtc(localPathsPath);
            if (cachedScreenshotDirectory is not null && modifiedUtc == lastLocalPathsModifiedUtc)
            {
                return cachedScreenshotDirectory;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(localPathsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("screenshot", out var screenshotNode) ||
                screenshotNode.ValueKind != JsonValueKind.String)
            {
                cachedScreenshotDirectory = null;
                lastLocalPathsModifiedUtc = modifiedUtc;
                return null;
            }

            cachedScreenshotDirectory = screenshotNode.GetString()?.Trim();
            lastLocalPathsModifiedUtc = modifiedUtc;
            return cachedScreenshotDirectory;
        }
        catch
        {
            cachedScreenshotDirectory = null;
            lastLocalPathsModifiedUtc = DateTime.MinValue;
            return null;
        }
    }

    private static ScreenshotCoordinate? ParseScreenshotFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return ScreenshotCoordinateParser.ParseFileName(fileName, GetModifiedAt(path));
    }

    private static long GetModifiedAt(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTime(path)).ToUnixTimeMilliseconds();
        }
        catch
        {
            return 0;
        }
    }

    private static int CompareCoordinate(ScreenshotCoordinate left, ScreenshotCoordinate right)
    {
        var order = left.Order.CompareTo(right.Order);
        if (order != 0)
        {
            return order;
        }

        var modified = left.ModifiedAt.CompareTo(right.ModifiedAt);
        return modified != 0
            ? modified
            : string.CompareOrdinal(left.FileName, right.FileName);
    }
}
