using System.IO;
using System.Reflection;

namespace TarkovMapLocator.Modules.MobileMap;

internal static class MobileMapRuntime
{
    internal static string ModuleDirectory
    {
        get
        {
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory) &&
                File.Exists(Path.Combine(assemblyDirectory, "web", "index.html")))
                return assemblyDirectory;

            var packagedDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "MobileMap");
            return packagedDirectory;
        }
    }

    internal static string WebDirectory => Path.Combine(ModuleDirectory, "web");
}
