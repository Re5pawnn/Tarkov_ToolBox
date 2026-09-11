using System.IO;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.TeamSync.Models;

namespace TarkovMapLocator.Modules.TeamSync.Services;

/// <summary>
/// Saves desktop-only LAN sync choices separately from the original tool.
/// </summary>
public sealed class LanSyncPreferencesService
{
    private readonly IFeatureHost _host;
    private readonly string _preferencePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public LanSyncPreferencesService(IFeatureHost host)
    {
        _host = host;
        _preferencePath = Path.Combine(host.DataDirectory, "lan-sync.json");
    }

    public LanSyncConfig Load()
    {
        try
        {
            if (!File.Exists(_preferencePath)) return LanSyncConfig.Default;
            var config = JsonSerializer.Deserialize<LanSyncConfig>(File.ReadAllText(_preferencePath), JsonOptions);
            return config?.Normalize() ?? LanSyncConfig.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "队友共享", "读取队友共享配置失败，已使用默认值", exception.ToString());
            return LanSyncConfig.Default;
        }
    }

    public bool Save(LanSyncConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_preferencePath)!;
            Directory.CreateDirectory(directory);
            var temporary = _preferencePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(config.Normalize(), JsonOptions));
            File.Move(temporary, _preferencePath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sync remains usable even if the user's profile cannot be written.
            _host.WriteLog(FeatureLogLevel.Warning, "队友共享", "保存队友共享配置失败，本次设置仅在当前会话生效", exception.ToString());
            return false;
        }
    }
}
