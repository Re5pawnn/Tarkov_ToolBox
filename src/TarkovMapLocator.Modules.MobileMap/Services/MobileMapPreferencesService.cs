using System.IO;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.MobileMap.Models;

namespace TarkovMapLocator.Modules.MobileMap.Services;

internal sealed class MobileMapPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IFeatureHost _host;
    private readonly string _path;

    internal MobileMapPreferencesService(IFeatureHost host)
    {
        _host = host;
        _path = Path.Combine(host.DataDirectory, "mobile-map.json");
    }

    internal MobileMapPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return MobileMapPreferences.Default;
            return (JsonSerializer.Deserialize<MobileMapPreferences>(File.ReadAllText(_path), JsonOptions) ?? MobileMapPreferences.Default).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "手机地图", "读取手机地图配置失败，已使用默认端口", exception.ToString());
            return MobileMapPreferences.Default;
        }
    }

    internal void Save(MobileMapPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences.Normalize(), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "手机地图", "保存手机地图配置失败", exception.ToString());
        }
    }
}
