using System.IO;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Stable identity shared by the promoted desktop application, its updater,
/// local settings, caches and runtime logs.
/// </summary>
internal static class ApplicationIdentity
{
    internal const string ApplicationDataFolderName = "TarkovMapLocatorDesktop";
    internal const string LegacyAutoOcrTestDataFolderName = "TarkovMapLocatorDesktopAutoOcrTest";

    internal static string ApplicationDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationDataFolderName);

    internal static string LegacyAutoOcrTestDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LegacyAutoOcrTestDataFolderName);
}
