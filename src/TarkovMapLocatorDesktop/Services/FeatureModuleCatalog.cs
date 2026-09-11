using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocatorDesktop.Services;

internal sealed class FeatureModuleCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, LoadedFeatureModule> _modules;

    private FeatureModuleCatalog(
        Dictionary<string, LoadedFeatureModule> modules,
        IReadOnlyList<FeatureModuleLoadIssue> issues)
    {
        _modules = modules;
        Issues = issues;
    }

    public static FeatureModuleCatalog Empty { get; } = new(
        new Dictionary<string, LoadedFeatureModule>(StringComparer.OrdinalIgnoreCase),
        []);

    public IReadOnlyCollection<LoadedFeatureModule> Modules => _modules.Values;

    public IReadOnlyList<FeatureModuleLoadIssue> Issues { get; }

    public bool TryGet(string id, out LoadedFeatureModule module) =>
        _modules.TryGetValue(id, out module!);

    public static FeatureModuleCatalog Discover(string modulesDirectory)
    {
        var modules = new Dictionary<string, LoadedFeatureModule>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<FeatureModuleLoadIssue>();
        if (!Directory.Exists(modulesDirectory)) return new FeatureModuleCatalog(modules, issues);

        foreach (var manifestPath in Directory.EnumerateFiles(modulesDirectory, "module.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifest = ReadManifest(manifestPath);
                var moduleDirectory = Path.GetDirectoryName(manifestPath)!;
                var assemblyPath = ResolveOwnedPath(moduleDirectory, manifest.Assembly);
                if (!File.Exists(assemblyPath))
                    throw new FileNotFoundException("组件入口程序集不存在。", assemblyPath);

                var loadContext = new FeatureAssemblyLoadContext(assemblyPath);
                var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
                var entryType = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
                if (!typeof(IFeatureModule).IsAssignableFrom(entryType))
                    throw new InvalidDataException($"入口类型未实现 {nameof(IFeatureModule)}：{manifest.EntryType}");
                if (Activator.CreateInstance(entryType) is not IFeatureModule instance)
                    throw new InvalidDataException($"无法创建组件入口：{manifest.EntryType}");
                if (!string.Equals(manifest.Id, instance.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"清单组件 ID“{manifest.Id}”与入口描述“{instance.Descriptor.Id}”不一致。");
                if (modules.ContainsKey(manifest.Id))
                    throw new InvalidDataException($"发现重复组件 ID：{manifest.Id}");

                modules.Add(manifest.Id, new LoadedFeatureModule(manifestPath, assemblyPath, instance, loadContext));
            }
            catch (Exception exception)
            {
                issues.Add(new FeatureModuleLoadIssue(manifestPath, exception.GetBaseException().Message));
            }
        }

        return new FeatureModuleCatalog(modules, issues);
    }

    private static FeatureModuleManifest ReadManifest(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<FeatureModuleManifest>(stream, JsonOptions)
                       ?? throw new InvalidDataException("组件清单为空。");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"不支持组件清单版本：{manifest.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException("组件清单缺少 ID。");
        if (string.IsNullOrWhiteSpace(manifest.Assembly)) throw new InvalidDataException("组件清单缺少入口程序集。");
        if (string.IsNullOrWhiteSpace(manifest.EntryType)) throw new InvalidDataException("组件清单缺少入口类型。");
        return manifest;
    }

    private static string ResolveOwnedPath(string moduleDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("组件清单不得使用绝对程序集路径。");
        var root = Path.GetFullPath(moduleDirectory) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(moduleDirectory, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("组件程序集路径超出自身目录。");
        return resolved;
    }

    private sealed class FeatureAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _moduleDirectory;

        public FeatureAssemblyLoadContext(string entryAssemblyPath)
            : base($"feature:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
            _moduleDirectory = Path.GetDirectoryName(entryAssemblyPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IFeatureModule).Assembly.GetName().Name, StringComparison.Ordinal))
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                var fileName = Path.GetFileName(unmanagedDllName);
                if (!Path.HasExtension(fileName)) fileName += ".dll";
                var moduleLocalPath = Path.Combine(_moduleDirectory, fileName);
                if (File.Exists(moduleLocalPath)) path = moduleLocalPath;
            }
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

internal sealed record LoadedFeatureModule(
    string ManifestPath,
    string AssemblyPath,
    IFeatureModule Instance,
    AssemblyLoadContext LoadContext);

internal sealed record FeatureModuleLoadIssue(string ManifestPath, string Message);

internal sealed class FeatureModuleManifest
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Assembly { get; init; } = string.Empty;
    public string EntryType { get; init; } = string.Empty;
}
