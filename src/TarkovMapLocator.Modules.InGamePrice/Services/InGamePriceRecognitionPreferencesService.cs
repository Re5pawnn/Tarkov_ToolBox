using System.IO;
using System.Text.Json;
using TarkovMapLocator.Modules.InGamePrice.Models;

namespace TarkovMapLocator.Modules.InGamePrice.Services;

public static class InGamePriceRecognitionPreferencesService
{
    private static readonly string StorageDirectory = Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "in-game-price");

    private static readonly string PreferencePath = Path.Combine(StorageDirectory, "recognition-settings.json");

    public static InGamePriceRecognitionSettings Load()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return InGamePriceRecognitionSettings.Default;
            return JsonSerializer.Deserialize<InGamePriceRecognitionSettings>(File.ReadAllText(PreferencePath))?.Normalize()
                   ?? InGamePriceRecognitionSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("游戏内查价", "读取识图配置失败，已使用默认值", exception.Message);
            return InGamePriceRecognitionSettings.Default;
        }
    }

    public static bool Save(InGamePriceRecognitionSettings settings)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            var temporaryPath = PreferencePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, PreferencePath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("游戏内查价", "保存识图配置失败，本次更改仅在当前会话生效", exception.Message);
            return false;
        }
    }

}
