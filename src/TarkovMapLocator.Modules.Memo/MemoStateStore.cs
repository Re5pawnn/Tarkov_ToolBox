using System.IO;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Memo;

public sealed record MemoItem(string Id, string Name, string ShortName, string IconLink, int Quantity);

public sealed record MemoState(
    int SchemaVersion,
    string Text,
    IReadOnlyList<MemoItem> Items,
    bool OverlayVisible,
    double? Left,
    double? Top)
{
    public static MemoState Empty { get; } = new(1, "", [], false, null, null);

    public MemoState Normalize()
    {
        var items = new List<MemoItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items ?? [])
        {
            if (items.Count == 100) break;
            var id = item.Id?.Trim() ?? "";
            var name = item.Name?.Trim() ?? "";
            if (id.Length == 0 || name.Length == 0 || !ids.Add(id)) continue;
            items.Add(new MemoItem(
                id,
                name.Length > 160 ? name[..160] : name,
                (item.ShortName ?? "").Trim(),
                (item.IconLink ?? "").Trim(),
                Math.Clamp(item.Quantity, 1, 9_999)));
        }

        var text = Text ?? "";
        return new MemoState(
            1,
            text.Length > 4_000 ? text[..4_000] : text,
            items,
            OverlayVisible,
            NormalizeCoordinate(Left),
            NormalizeCoordinate(Top));
    }

    private static double? NormalizeCoordinate(double? value) =>
        value is { } coordinate && double.IsFinite(coordinate) && coordinate is >= -100_000 and <= 100_000
            ? coordinate
            : null;
}

internal sealed class MemoStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly IFeatureHost _host;

    public MemoStateStore(IFeatureHost host)
    {
        _host = host;
        _path = Path.Combine(host.DataDirectory, "memo", "memo-state.json");
    }

    public MemoState Load()
    {
        try
        {
            if (!File.Exists(_path)) return MemoState.Empty;
            var state = JsonSerializer.Deserialize<MemoState>(File.ReadAllText(_path), JsonOptions);
            return state?.SchemaVersion == 1 ? state.Normalize() : MemoState.Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "备忘录", "读取备忘录失败，已使用空白内容", exception.ToString());
            return MemoState.Empty;
        }
    }

    public bool Save(MemoState state)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state.Normalize(), JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _host.WriteLog(FeatureLogLevel.Warning, "备忘录", "保存备忘录失败", exception.ToString());
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath)) try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _host.WriteLog(FeatureLogLevel.Warning, "备忘录", "清理备忘录临时文件失败", exception.Message);
            }
        }
    }
}
