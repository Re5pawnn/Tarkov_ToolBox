using System.IO;
using System.Globalization;
using System.Text.Json;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

public sealed record LocalMapPointLoadResult(
    IReadOnlyDictionary<string, IReadOnlyList<MapMarker>> MarkersByMap,
    IReadOnlyDictionary<string, MapCoordinateBounds> BoundsByMap,
    int TotalMarkerCount,
    string? ErrorMessage)
{
    public bool IsAvailable => ErrorMessage is null && MarkersByMap.Count > 0;
}

/// <summary>
/// Reads the same local map metadata file used by the packaged application.
/// The coordinates are projected with the source application's MapProjection rule.
/// </summary>
public static class LocalMapPointService
{
    private static readonly Lazy<LocalMapPointLoadResult> Cached = new(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static LocalMapPointLoadResult Load() => Cached.Value;

    private static LocalMapPointLoadResult LoadCore()
    {
        var detailFiles = FindMapDetailFiles();
        if (detailFiles.Count == 0)
        {
            return new LocalMapPointLoadResult(new Dictionary<string, IReadOnlyList<MapMarker>>(), new Dictionary<string, MapCoordinateBounds>(), 0, "未找到 maps_detail.json");
        }

        try
        {
            var maps = new Dictionary<string, IReadOnlyList<MapMarker>>(StringComparer.OrdinalIgnoreCase);
            var boundsByMap = new Dictionary<string, MapCoordinateBounds>(StringComparer.OrdinalIgnoreCase);

            foreach (var detailFile in detailFiles)
            {
                using var stream = File.OpenRead(detailFile);
                using var document = JsonDocument.Parse(stream);
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (!TryGetMapData(entry.Value, out var data)) continue;

                    var key = ReadString(data, "key");
                    if (string.IsNullOrWhiteSpace(key)) key = entry.Name;
                    if (!TryReadBounds(key, data, out var bounds)) continue;
                    var markers = ReadMarkers(key, data, bounds).ToArray();
                    boundsByMap[key] = bounds;
                    maps[key] = markers;
                }
            }

            MergeSwitchMarkers(maps, boundsByMap);
            MergeSeasonDocumentMarkers(maps, boundsByMap);
            foreach (var mapKey in maps.Keys.ToArray())
                maps[mapKey] = MergeDuplicateMarkers(mapKey, maps[mapKey]);

            return new LocalMapPointLoadResult(maps, boundsByMap, maps.Values.Sum(points => points.Count), null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new LocalMapPointLoadResult(new Dictionary<string, IReadOnlyList<MapMarker>>(), new Dictionary<string, MapCoordinateBounds>(), 0, $"读取 maps_detail.json 失败：{exception.Message}");
        }
    }

    private static IReadOnlyList<MapMarker> MergeDuplicateMarkers(string mapKey, IReadOnlyList<MapMarker> markers)
    {
        var merged = new List<MapMarker>(markers.Count);
        foreach (var group in markers.GroupBy(marker => (
                     marker.Type,
                     marker.Label,
                     X: Math.Round(marker.WorldX ?? marker.X, 4),
                     Z: Math.Round(marker.WorldZ ?? marker.Y, 4))))
        {
            var first = group.First();
            var factions = group
                .Select(marker => marker.Faction)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var toolTips = group
                .Select(marker => marker.ToolTipText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var faction = factions.Length switch
            {
                0 => null,
                1 => factions[0],
                _ => "both"
            };
            var toolTip = first.Type is "extract" or "transit"
                ? ExtractionRequirementService.BuildToolTip(mapKey, first.Type, first.Label, faction)
                : toolTips.Length == 0 ? null : string.Join("\n\n", toolTips);
            merged.Add(new MapMarker
            {
                Type = first.Type,
                Label = first.Label,
                Faction = faction,
                ToolTipText = toolTip,
                PreviewImageId = first.PreviewImageId,
                PreviewImageUrl = first.PreviewImageUrl,
                X = first.X,
                Y = first.Y,
                ShowLabel = first.ShowLabel,
                VisibleOnSurface = group.Any(marker => marker.VisibleOnSurface),
                HeadingDegrees = first.HeadingDegrees,
                ColorHex = first.ColorHex,
                WorldX = first.WorldX,
                WorldHeight = first.WorldHeight,
                WorldZ = first.WorldZ
            });
        }

        return merged;
    }

    private static IEnumerable<MapMarker> ReadMarkers(string mapKey, JsonElement data, MapCoordinateBounds bounds)
    {
        // Tasks are loaded separately on demand. Normal extracts and map-to-map
        // transit extracts live in different arrays in maps_detail.json. Locked
        // doors, trunks and containers are opt-in in the UI, so retaining the
        // complete lock list here does not clutter the default map.
        foreach (var marker in ReadExtracts(mapKey, data, bounds)
                     .Concat(ReadTransits(mapKey, data, bounds))
                     .Concat(ReadLocks(mapKey, data, bounds))
                     .Concat(ReadManualMarkers(mapKey, data, bounds)))
            yield return marker;
    }

    private static IEnumerable<MapMarker> ReadManualMarkers(string mapKey, JsonElement data, MapCoordinateBounds bounds)
    {
        if (string.Equals(mapKey, "interchange", StringComparison.OrdinalIgnoreCase))
        {
            // The local metadata does not contain Interchange's flare extraction.
            // These normalized coordinates are calibrated against interchange.png,
            // so the point remains anchored while the map is zoomed or panned.
            yield return new MapMarker
            {
                Type = "extract",
                Label = "河畔之路（信号弹）",
                Faction = "pmc",
                ToolTipText = ExtractionRequirementService.BuildToolTip(mapKey, "extract", "河畔之路（信号弹）", "pmc"),
                X = 0.08810,
                Y = 0.60643,
                ShowLabel = true
            };
        }

        if (!string.Equals(mapKey, "reserve", StringComparison.OrdinalIgnoreCase) ||
            GetArray(data, "locks").Any(item =>
                item.TryGetProperty("key", out var key) &&
                string.Equals(ReadString(key, "id"), "68e9654d72488961110dbf69", StringComparison.OrdinalIgnoreCase)) ||
            !bounds.TryProject(-109.2, 22.3, out var x, out var y))
            yield break;

        // Added to the live source after the local map snapshot was created.
        yield return new MapMarker
        {
            Type = "key-room",
            Label = "RB-PKPTS 钥匙",
            ToolTipText = BuildLockToolTip("door", "RB-PKPTS 钥匙", needsPower: false),
            X = x,
            Y = y,
            ShowLabel = false,
            WorldX = -109.2,
            WorldZ = 22.3
        };
    }

    private static IEnumerable<MapMarker> ReadExtracts(string mapKey, JsonElement data, MapCoordinateBounds bounds)
    {
        foreach (var item in GetArray(data, "extracts"))
        {
            var label = NormalizeExtractLabel(ReadFirstString(item, "name", "id"));
            if (string.Equals(mapKey, "customs", StringComparison.OrdinalIgnoreCase))
            {
                label = label switch
                {
                    "铁路通道" => "铁路通道（信号弹）",
                    "走私者的地堡 (ZB-1012)" => "走私者地堡（ZB-1012）",
                    _ => label
                };
            }
            var visibleOnSurface = string.Equals(mapKey, "customs", StringComparison.OrdinalIgnoreCase) &&
                                   (string.Equals(label, "ZB-013", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(label, "ZB-1011", StringComparison.OrdinalIgnoreCase) ||
                                    label.Contains("ZB-1012", StringComparison.OrdinalIgnoreCase));
            if (TryCreateMarker(mapKey, item, bounds, "extract", label, true, out var marker, visibleOnSurface: visibleOnSurface))
                yield return marker;
        }
    }

    private static IEnumerable<MapMarker> ReadTransits(string mapKey, JsonElement data, MapCoordinateBounds bounds)
    {
        foreach (var item in GetArray(data, "transits"))
        {
            var label = NormalizeTransitLabel(ReadFirstString(item, "description", "name", "id"));
            if (TryCreateMarker(mapKey, item, bounds, "transit", label, true, out var marker))
                yield return marker;
        }
    }

    private static IEnumerable<MapMarker> ReadLocks(string mapKey, JsonElement data, MapCoordinateBounds bounds)
    {
        foreach (var item in GetArray(data, "locks"))
        {
            if (!item.TryGetProperty("key", out var key)) continue;
            var label = ReadFirstString(key, "name", "normalizedName", "id");
            if (string.IsNullOrWhiteSpace(label)) continue;

            var lockType = ReadString(item, "lockType");
            var needsPower = item.TryGetProperty("needsPower", out var power) &&
                             power.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                             power.GetBoolean();
            if (TryCreateMarker(
                    mapKey,
                    item,
                    bounds,
                    "key-room",
                    label,
                    false,
                    out var marker,
                    BuildLockToolTip(lockType, label, needsPower)))
                yield return marker;
        }
    }

    private static void MergeSwitchMarkers(
        IDictionary<string, IReadOnlyList<MapMarker>> maps,
        IReadOnlyDictionary<string, MapCoordinateBounds> boundsByMap)
    {
        var switchFile = FindDataFile("map-switches.json");
        if (switchFile is null) return;

        using var stream = File.OpenRead(switchFile);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("maps", out var mapEntries) || mapEntries.ValueKind != JsonValueKind.Array) return;

        foreach (var mapEntry in mapEntries.EnumerateArray())
        {
            var mapKey = ReadString(mapEntry, "key");
            if (string.IsNullOrWhiteSpace(mapKey) || !boundsByMap.TryGetValue(mapKey, out var bounds)) continue;

            var switches = ReadSwitches(mapKey, mapEntry, bounds).ToArray();
            if (switches.Length == 0) continue;
            maps.TryGetValue(mapKey, out var existing);
            maps[mapKey] = (existing ?? []).Concat(switches).ToArray();
        }
    }

    private static IEnumerable<MapMarker> ReadSwitches(string mapKey, JsonElement data, MapCoordinateBounds bounds)
    {
        foreach (var item in GetArray(data, "switches"))
        {
            var sourceName = ReadFirstString(item, "name", "id");
            if (string.IsNullOrWhiteSpace(sourceName)) continue;
            if (string.Equals(mapKey, "the-lab", StringComparison.OrdinalIgnoreCase) &&
                (sourceName.EndsWith(" Elevator Call Button", StringComparison.OrdinalIgnoreCase) ||
                 sourceName.EndsWith(" Elevator Extract Button", StringComparison.OrdinalIgnoreCase)))
                continue;
            var label = TranslateSwitchLabel(sourceName);
            if (TryCreateMarker(mapKey, item, bounds, "switch", label, true, out var marker, BuildSwitchToolTip(item, label)))
                yield return marker;
        }
    }

    private static void MergeSeasonDocumentMarkers(
        IDictionary<string, IReadOnlyList<MapMarker>> maps,
        IReadOnlyDictionary<string, MapCoordinateBounds> boundsByMap)
    {
        var file = FindDataFile("map-season-documents.json");
        if (file is null) return;

        using var stream = File.OpenRead(file);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("maps", out var mapEntries) || mapEntries.ValueKind != JsonValueKind.Array) return;

        foreach (var mapEntry in mapEntries.EnumerateArray())
        {
            var mapKey = ReadString(mapEntry, "key");
            if (string.IsNullOrWhiteSpace(mapKey) || !boundsByMap.TryGetValue(mapKey, out var bounds)) continue;

            var markers = GetArray(mapEntry, "documents")
                .Select(item => TryCreateSeasonDocumentMarker(mapKey, item, bounds, out var marker) ? marker : null)
                .OfType<MapMarker>()
                .ToArray();
            if (markers.Length == 0) continue;
            maps.TryGetValue(mapKey, out var existing);
            maps[mapKey] = (existing ?? []).Concat(markers).ToArray();
        }
    }

    private static bool TryCreateSeasonDocumentMarker(
        string mapKey,
        JsonElement item,
        MapCoordinateBounds bounds,
        out MapMarker marker)
    {
        marker = null!;
        var label = ReadString(item, "name");
        if (string.IsNullOrWhiteSpace(label) || !TryReadPosition(item, out var x, out var height, out var z)) return false;
        var season = TryReadNumber(item, "season", out var value) ? (int)value : 1;
        var heightText = height is { } h ? h.ToString("0.##", CultureInfo.InvariantCulture) : "--";
        var toolTip = $"赛季：{season}\n坐标：x {x.ToString("0.##", CultureInfo.InvariantCulture)}, z {z.ToString("0.##", CultureInfo.InvariantCulture)}, h {heightText}";
        if (!TryCreateMarker(mapKey, item, bounds, "season-document", label, true, out var created, toolTip)) return false;
        marker = new MapMarker
        {
            Type = created.Type,
            Label = created.Label,
            Faction = created.Faction,
            ToolTipText = created.ToolTipText,
            PreviewImageId = ReadString(item, "id"),
            PreviewImageUrl = ReadString(item, "imageUrl"),
            X = created.X,
            Y = created.Y,
            ShowLabel = created.ShowLabel,
            VisibleOnSurface = created.VisibleOnSurface,
            HeadingDegrees = created.HeadingDegrees,
            ColorHex = created.ColorHex,
            WorldX = created.WorldX,
            WorldHeight = created.WorldHeight,
            WorldZ = created.WorldZ
        };
        return true;
    }

    private static string BuildSwitchToolTip(JsonElement item, string label)
    {
        var lines = new List<string> { $"拉闸点：{label}" };
        var switchType = ReadString(item, "switchType");
        if (!string.IsNullOrWhiteSpace(switchType))
            lines.Add($"操作：{TranslateSwitchOperation(switchType)}");

        if (item.TryGetProperty("activatedBy", out var activatedBy) && activatedBy.ValueKind == JsonValueKind.Object)
        {
            var prerequisite = ReadFirstString(activatedBy, "name", "id");
            if (!string.IsNullOrWhiteSpace(prerequisite))
                lines.Add($"前置：{TranslateSwitchLabel(prerequisite)}");
        }

        if (item.TryGetProperty("activates", out var activates) && activates.ValueKind == JsonValueKind.Array)
        {
            var effects = activates.EnumerateArray()
                .Select(effect =>
                {
                    var operation = ReadString(effect, "operation");
                    if (!effect.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object) return "";
                    var targetName = ReadFirstString(target, "name", "id");
                    return string.IsNullOrWhiteSpace(targetName)
                        ? ""
                        : $"{TranslateSwitchOperation(operation)} {TranslateSwitchLabel(targetName)}";
                })
                .Where(effect => !string.IsNullOrWhiteSpace(effect))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (effects.Length > 0) lines.Add($"作用：{string.Join("；", effects)}");
        }

        return string.Join('\n', lines);
    }

    private static string TranslateSwitchOperation(string operation) => operation.Trim().ToLowerInvariant() switch
    {
        "open" => "开启",
        "close" => "关闭",
        "unlock" => "解锁",
        "lock" => "锁定",
        _ => operation.Trim()
    };

    private static string TranslateSwitchLabel(string label) => label.Trim() switch
    {
        "ZB-013 Power Switch" => "ZB-013 电源拉闸",
        "Mall Main Power Switch" => "商场主电源",
        "Non-Kiba Alarms Switch" => "非 KIBA 警报开关",
        "Alarms Switch" => "警报开关",
        "Saferoom Exfil Switch" => "安全屋撤离开关",
        "Saferoom Exfil Unlock Switch" => "安全屋解锁开关",
        "Object 14 Container Switch" => "14 号集装箱开关",
        "Med Elevator Power Button" => "医疗电梯拉闸点",
        "Main Elevator Call Button" => "主电梯呼叫按钮",
        "Med Elevator Call Button" => "医疗电梯呼叫按钮",
        "Hangar Gate Switch" => "库房大门开关",
        "Cargo Elevator Call Button" => "货运电梯呼叫按钮",
        "Cargo Elevator Extract Button" => "货运电梯撤离按钮",
        "Main Elevator Power Button" => "主电梯拉闸点",
        "Water Level Switch" => "水位控制开关",
        "Main Elevator Extract Button" => "主电梯撤离按钮",
        "Parking Gate Switch" => "停车场大门开关",
        "Sewage Conduit Pump Button" => "污水管道泵按钮",
        "Cargo Elevator Power Button" => "货运电梯拉闸点",
        "Med Elevator Extract Button" => "医疗电梯撤离按钮",
        "Alarm Switch" => "警报开关",
        "Containment Block Power Switch" => "收容区电源开关",
        "Lightkeeper Switch" => "灯塔守卫开关",
        "Bunker Hermetic Door Power Switch" => "地堡密闭门电源开关",
        "D-2 Power Switch" => "D-2 电源开关",
        "D-2 Door Switch" => "D-2 门开关",
        "Sealed Door" => "密封门开关",
        "Fire Trap Switch" => "火焰陷阱开关",
        "Toxic Pool Trap Switch" => "毒池陷阱开关",
        "Shotgun Trap Switch" => "霰弹枪陷阱开关",
        "Toxic Puddle Trap Switch" => "毒水坑陷阱开关",
        "Steam Trap Switch" => "蒸汽陷阱开关",
        _ => label.Trim()
    };

    private static string BuildLockToolTip(string lockType, string keyName, bool needsPower)
    {
        var target = lockType.ToLowerInvariant() switch
        {
            "door" => "房门",
            "trunk" => "载具后备箱",
            "container" => "上锁容器",
            "switch" => "机关 / 控制装置",
            _ => "上锁点"
        };
        return $"开启对象：{target}\n需要钥匙：{keyName}" +
               (needsPower ? "\n额外条件：需要供电" : "");
    }

    private static string NormalizeExtractLabel(string label)
    {
        var normalized = label
            .Replace("撤离点", "", StringComparison.Ordinal)
            .Trim()
            .Trim(' ', '·', '-', '—', ':', '：');
        return string.IsNullOrWhiteSpace(normalized) ? "未命名出口" : normalized;
    }

    private static string NormalizeTransitLabel(string label)
    {
        var normalized = label.Trim().TrimEnd('?', '？');
        if (normalized.StartsWith("Transit to ", StringComparison.OrdinalIgnoreCase))
        {
            var destination = normalized["Transit to ".Length..].Trim();
            normalized = string.Equals(destination, "Terminal", StringComparison.OrdinalIgnoreCase)
                ? "前往码头"
                : $"前往 {destination}";
        }
        else if (normalized.StartsWith("转移到", StringComparison.Ordinal))
        {
            normalized = "前往" + normalized["转移到".Length..].Trim();
        }

        return NormalizeExtractLabel(normalized);
    }

    private static bool TryCreateMarker(
        string mapKey,
        JsonElement item,
        MapCoordinateBounds bounds,
        string type,
        string label,
        bool showLabel,
        out MapMarker marker,
        string? toolTipText = null,
        bool visibleOnSurface = false)
    {
        marker = null!;
        if (!TryReadPosition(item, out var x, out var height, out var z)) return false;

        if (!bounds.TryProject(x, z, out var u, out var v)) return false;

        var faction = ReadString(item, "faction");
        marker = new MapMarker
        {
            Type = type,
            Label = label.Trim(),
            Faction = string.IsNullOrWhiteSpace(faction) ? null : faction,
            ToolTipText = toolTipText ?? ExtractionRequirementService.BuildToolTip(mapKey, type, label, faction),
            X = u,
            Y = v,
            ShowLabel = showLabel,
            VisibleOnSurface = visibleOnSurface,
            WorldX = x,
            WorldHeight = height,
            WorldZ = z
        };
        return true;
    }

    private static bool TryGetMapData(JsonElement entry, out JsonElement data)
    {
        data = default;
        return entry.ValueKind == JsonValueKind.Object &&
               entry.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Object &&
               raw.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static bool TryReadBounds(string mapKey, JsonElement data, out MapCoordinateBounds bounds)
    {
        bounds = default;
        if (!data.TryGetProperty("bounds", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2 ||
            !TryReadArrayPosition(value[0], out var x0, out var z0) || !TryReadArrayPosition(value[1], out var x1, out var z1)) return false;

        var coordinateRotation = TryReadNumber(data, "coordinateRotation", out var rotation) ? rotation : 0;
        // Factory's SVG is quarter-turned relative to its world-coordinate bounds.
        // The source metadata reports that difference as 90 degrees, but the old
        // projector only used the value for metadata and left every marker rotated.
        var positionRotation = string.Equals(mapKey, "factory", StringComparison.OrdinalIgnoreCase)
            ? 90
            : 0;
        bounds = new MapCoordinateBounds(
            x0,
            z0,
            x1,
            z1,
            data.TryGetProperty("reverseCoordinate", out var reverse) && reverse.ValueKind is JsonValueKind.True or JsonValueKind.False && reverse.GetBoolean(),
            coordinateRotation,
            positionRotation);
        return true;
    }

    private static bool TryReadPosition(JsonElement item, out double x, out double? height, out double z)
    {
        x = z = 0;
        height = null;
        var value = item;
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("position", out var position)) value = position;

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!TryReadNumber(value, "x", out x) || !TryReadNumber(value, "z", out z)) return false;
            if (TryReadNumber(value, "y", out var worldHeight)) height = worldHeight;
            return true;
        }
        return TryReadArrayPosition(value, out x, out z);
    }

    private static bool TryReadArrayPosition(JsonElement value, out double x, out double z)
    {
        x = z = 0;
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2 &&
               value[0].TryGetDouble(out x) && value[1].TryGetDouble(out z);
    }

    private static IEnumerable<JsonElement> GetArray(JsonElement data, string name) =>
        data.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
            : [];

    private static string ReadFirstString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.String)
            {
                var text = candidate.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return "";
    }

    private static string ReadString(JsonElement value, string name) => ReadFirstString(value, name);

    private static bool TryReadNumber(JsonElement value, string name, out double number)
    {
        number = 0;
        return value.TryGetProperty(name, out var candidate) && candidate.TryGetDouble(out number);
    }

    private static IReadOnlyList<string> FindMapDetailFiles()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var primary = Path.Combine(directory.FullName, "maps_detail.json");
                if (!File.Exists(primary)) continue;

                var files = new List<string> { primary };
                var supplemental = Path.Combine(directory.FullName, "maps_detail.supplemental.json");
                if (File.Exists(supplemental)) files.Add(supplemental);
                return files;
            }
        }
        return [];
    }

    private static string? FindDataFile(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, fileName);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

}
