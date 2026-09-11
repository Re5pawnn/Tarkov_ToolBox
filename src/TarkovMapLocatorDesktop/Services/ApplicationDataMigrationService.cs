using System.IO;

namespace TarkovMapLocatorDesktop.Services;

public sealed record ApplicationDataMigrationResult(int CopiedFileCount, string? ErrorMessage);

/// <summary>
/// One-time merge used after the automatic-OCR test build became the main
/// application. Newer test-profile files win, while newer existing stable
/// settings are never overwritten.
/// </summary>
public static class ApplicationDataMigrationService
{
    private const string MarkerFileName = ".auto-ocr-profile-promoted-v1";

    public static ApplicationDataMigrationResult MigratePromotedProfile()
    {
        var source = ApplicationIdentity.LegacyAutoOcrTestDataDirectory;
        var target = ApplicationIdentity.ApplicationDataDirectory;
        var marker = Path.Combine(target, MarkerFileName);
        if (File.Exists(marker)) return new ApplicationDataMigrationResult(0, null);

        try
        {
            Directory.CreateDirectory(target);
            var copied = 0;
            if (Directory.Exists(source))
            {
                foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(source, sourceFile);
                    if (string.Equals(relativePath, MarkerFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var targetFile = Path.Combine(target, relativePath);
                    if (File.Exists(targetFile) &&
                        File.GetLastWriteTimeUtc(targetFile) >= File.GetLastWriteTimeUtc(sourceFile))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                    var temporaryFile = $"{targetFile}.{Environment.ProcessId}.migration.tmp";
                    try
                    {
                        File.Copy(sourceFile, temporaryFile, overwrite: true);
                        File.Move(temporaryFile, targetFile, overwrite: true);
                        copied++;
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
                        catch { }
                    }
                }
            }

            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
            return new ApplicationDataMigrationResult(copied, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ApplicationDataMigrationResult(0, exception.Message);
        }
    }
}
