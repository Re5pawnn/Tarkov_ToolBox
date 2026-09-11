using System.IO;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Utilities.StoryGuide;

internal sealed class StoryGuidePreferencesService(IFeatureHost host)
{
    private readonly string _path = Path.Combine(host.DataDirectory, "story-guide-mode.json");

    internal string Load()
    {
        try
        {
            return File.Exists(_path)
                ? Normalize(JsonSerializer.Deserialize<string>(File.ReadAllText(_path)))
                : "pve";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            host.WriteLog(FeatureLogLevel.Warning, "剧情攻略", "读取攻略模式失败，已使用 PVE", exception.Message);
            return "pve";
        }
    }

    internal void Save(string mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(mode)));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            host.WriteLog(FeatureLogLevel.Warning, "剧情攻略", "保存攻略模式失败，本次选择仅在当前会话生效", exception.Message);
        }
    }

    private static string Normalize(string? mode) =>
        string.Equals(mode, "pvp", StringComparison.OrdinalIgnoreCase) ? "pvp" : "pve";
}
