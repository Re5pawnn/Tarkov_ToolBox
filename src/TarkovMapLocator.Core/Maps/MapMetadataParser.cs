using System.Text.Json;

namespace TarkovMapLocator.Core.Maps;

public static class MapMetadataParser
{
    private const int MaxNativeExtractCount = 40;
    private const int MaxNativeLabelCount = 80;
    private const int MaxNativeMoreCount = 700;

    public static IReadOnlyList<MapMetadata> ParseMaps(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var maps = new List<MapMetadata>();
        foreach (var mapNode in root.EnumerateObject())
        {
            var metadata = ParseMapEntry(mapNode.Name, mapNode.Value);
            if (metadata is not null)
            {
                maps.Add(metadata);
            }
        }

        return maps;
    }

    private static MapMetadata? ParseMapEntry(string idFromProperty, JsonElement entry)
    {
        var data = entry.TryGetProperty("raw", out var rawNode) &&
            rawNode.TryGetProperty("data", out var rawDataNode)
                ? rawDataNode
                : entry;

        if (!TryReadBounds(data, out var x0, out var z0, out var x1, out var z1))
        {
            return null;
        }

        var id = ReadString(data, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = idFromProperty;
        }

        var key = ReadString(data, "key");
        var normalizedName = ReadString(data, "normalizedName");
        var nameId = ReadString(data, "nameId");
        var name = ReadString(data, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(entry, "name");
        }

        return new MapMetadata(
            id,
            key,
            string.IsNullOrWhiteSpace(name) ? key : name,
            normalizedName,
            nameId,
            new MapBounds(
                x0,
                z0,
                x1,
                z1,
                data.TryGetProperty("reverseCoordinate", out var reverseNode) &&
                    reverseNode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    reverseNode.GetBoolean()),
            ReadString(data, "svgPath"),
            ReadString(data, "tilePath"),
            ReadHeightRange(data),
            ReadLayers(data),
            ReadNativePrototypePois(data),
            ReadNativePrototypePoiSources(data));
    }

    private static MapHeightRange? ReadHeightRange(JsonElement data)
    {
        return TryReadHeightRange(data, out var minY, out var maxY)
            ? new MapHeightRange(minY, maxY)
            : null;
    }

    private static IReadOnlyList<MapLayerMetadata> ReadLayers(JsonElement data)
    {
        if (!data.TryGetProperty("layers", out var layersNode) ||
            layersNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var layers = new List<MapLayerMetadata>();
        foreach (var layerNode in layersNode.EnumerateArray())
        {
            if (layerNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(layerNode, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            layers.Add(new MapLayerMetadata(
                name,
                ReadString(layerNode, "svgLayer"),
                ReadString(layerNode, "tilePath"),
                layerNode.TryGetProperty("show", out var showNode) &&
                    showNode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    showNode.GetBoolean(),
                ReadLayerExtents(layerNode)));
        }

        return layers;
    }

    private static IReadOnlyList<MapLayerExtent> ReadLayerExtents(JsonElement layerNode)
    {
        if (!layerNode.TryGetProperty("extents", out var extentsNode) ||
            extentsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var extents = new List<MapLayerExtent>();
        foreach (var extentNode in extentsNode.EnumerateArray())
        {
            if (extentNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            extents.Add(new MapLayerExtent(
                ReadExtentHeight(extentNode),
                ReadExtentBounds(extentNode)));
        }

        return extents;
    }

    private static MapHeightRange? ReadExtentHeight(JsonElement extentNode)
    {
        if (!extentNode.TryGetProperty("height", out var heightNode) ||
            heightNode.ValueKind != JsonValueKind.Array ||
            heightNode.GetArrayLength() < 2 ||
            !TryReadNumber(heightNode[0], out var minY) ||
            !TryReadNumber(heightNode[1], out var maxY))
        {
            return null;
        }

        if (minY > maxY)
        {
            (minY, maxY) = (maxY, minY);
        }

        return new MapHeightRange(minY, maxY);
    }

    private static IReadOnlyList<MapLayerBounds> ReadExtentBounds(JsonElement extentNode)
    {
        if (!extentNode.TryGetProperty("bounds", out var boundsNode) ||
            boundsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var bounds = new List<MapLayerBounds>();
        foreach (var boundsEntry in boundsNode.EnumerateArray())
        {
            if (TryReadLayerBounds(boundsEntry, out var item))
            {
                bounds.Add(item);
            }
        }

        return bounds;
    }

    private static bool TryReadLayerBounds(JsonElement boundsEntry, out MapLayerBounds item)
    {
        item = new MapLayerBounds(0, 0, 0, 0, "");
        if (boundsEntry.ValueKind != JsonValueKind.Array ||
            boundsEntry.GetArrayLength() < 2 ||
            !TryReadPositionValue(boundsEntry[0], out var x0, out var z0) ||
            !TryReadPositionValue(boundsEntry[1], out var x1, out var z1))
        {
            return false;
        }

        var label = boundsEntry.GetArrayLength() >= 3 && boundsEntry[2].ValueKind == JsonValueKind.String
            ? boundsEntry[2].GetString()?.Trim() ?? ""
            : "";
        item = new MapLayerBounds(x0, z0, x1, z1, label);
        return true;
    }

    private static IReadOnlyList<MapPoi> ReadNativePrototypePois(JsonElement data)
    {
        return ReadExtractPois(data)
            .Take(MaxNativeExtractCount)
            .Concat(ReadLabelPois(data).Take(MaxNativeLabelCount))
            .Concat(ReadMorePois(data).Take(MaxNativeMoreCount))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadNativePrototypePoiSources(JsonElement data)
    {
        var sources = new List<string>();
        if (ReadExtractPois(data).Any())
        {
            sources.Add(MapPoiSources.Extracts);
        }

        if (ReadLabelPois(data).Any())
        {
            sources.Add(MapPoiSources.Labels);
        }

        if (ReadMorePois(data).Any())
        {
            sources.Add(MapPoiSources.More);
        }

        return sources.Count > 0 ? sources : [MapPoiSources.Extracts];
    }

    private static IEnumerable<MapPoi> ReadExtractPois(JsonElement data)
    {
        if (data.TryGetProperty("extracts", out var extractsNode) &&
            extractsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var extract in extractsNode.EnumerateArray())
            {
                if (!TryReadPosition(extract, out var x, out var z))
                {
                    continue;
                }

                var displayName = GetExtractDisplayName(extract);
                yield return new MapPoi(
                    $"[撤离点] {displayName}",
                    x,
                    z,
                    "extract",
                    MapPoiSources.Extracts,
                    GetExtractIconName(extract),
                    true,
                    displayName);
            }
        }

        foreach (var transit in ReadTransitPois(data, MapPoiSources.Extracts))
        {
            yield return transit;
        }
    }

    private static IEnumerable<MapPoi> ReadLabelPois(JsonElement data)
    {
        if (!data.TryGetProperty("labels", out var labelsNode) ||
            labelsNode.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var hasHeightRange = TryReadHeightRange(data, out var minY, out var maxY);
        foreach (var labelNode in labelsNode.EnumerateArray())
        {
            if (!TryReadPosition(labelNode, out var x, out var z))
            {
                continue;
            }

            if (hasHeightRange &&
                TryReadPositionY(labelNode, out var y) &&
                (y < minY || y > maxY))
            {
                continue;
            }

            var label = ReadFirstString(labelNode, "label", "name", "text", "title", "id");
            yield return new MapPoi(
                string.IsNullOrWhiteSpace(label) ? "Label" : label,
                x,
                z,
                "label",
                MapPoiSources.Labels);
        }
    }

    private static IEnumerable<MapPoi> ReadMorePois(JsonElement data)
    {
        foreach (var poi in ReadSpawnPois(data))
        {
            yield return poi;
        }

        foreach (var poi in ReadObjectPois(data, "locks", "locks", "钥匙门", "key", "name"))
        {
            yield return poi;
        }

        foreach (var poi in ReadObjectPois(data, "switches", "switches", "开关", "name", "id"))
        {
            yield return poi;
        }

        foreach (var poi in ReadObjectPois(data, "hazards", "hazards", "危险区", "name", "hazardType", "id"))
        {
            yield return poi;
        }

        foreach (var poi in ReadObjectPois(data, "stationaryWeapons", "stationary", "固定武器", "stationaryWeapon", "name"))
        {
            yield return poi;
        }

        foreach (var poi in ReadObjectPois(data, "btrStops", "btr", "BTR", "name", "id"))
        {
            yield return poi;
        }

        foreach (var poi in ReadTransitPois(data, MapPoiSources.More))
        {
            yield return poi;
        }
    }

    private static IEnumerable<MapPoi> ReadTransitPois(JsonElement data, string source)
    {
        if (!data.TryGetProperty("transits", out var transitsNode) ||
            transitsNode.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var transit in transitsNode.EnumerateArray())
        {
            if (!TryReadPosition(transit, out var x, out var z))
            {
                continue;
            }

            var displayName = GetTransitDisplayName(transit);
            yield return new MapPoi(
                $"[转移点] {displayName}",
                x,
                z,
                "transits",
                source,
                "extract_transit",
                true,
                displayName);
        }
    }

    private static IEnumerable<MapPoi> ReadSpawnPois(JsonElement data)
    {
        if (!data.TryGetProperty("spawns", out var spawnsNode) ||
            spawnsNode.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var hasHeightRange = TryReadHeightRange(data, out var minY, out var maxY);
        foreach (var spawn in spawnsNode.EnumerateArray())
        {
            if (!TryReadPosition(spawn, out var x, out var z))
            {
                continue;
            }

            if (hasHeightRange &&
                TryReadPositionY(spawn, out var y) &&
                (y < minY || y > maxY))
            {
                continue;
            }

            var kind = GetSpawnKind(spawn);
            var label = ReadFirstString(spawn, "zoneName", "name", "id");
            yield return new MapPoi(
                string.IsNullOrWhiteSpace(label) ? "出生点" : $"出生点 {label}",
                x,
                z,
                kind,
                MapPoiSources.More);
        }
    }

    private static IEnumerable<MapPoi> ReadObjectPois(JsonElement data, string collectionName, string kind, string fallbackLabel, params string[] labelPaths)
    {
        if (!data.TryGetProperty(collectionName, out var collectionNode) ||
            collectionNode.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var hasHeightRange = TryReadHeightRange(data, out var minY, out var maxY);
        foreach (var item in collectionNode.EnumerateArray())
        {
            if (!TryReadPosition(item, out var x, out var z))
            {
                continue;
            }

            if (hasHeightRange &&
                TryReadPositionY(item, out var y) &&
                (y < minY || y > maxY))
            {
                continue;
            }

            var label = ReadNestedFirstString(item, labelPaths);
            yield return new MapPoi(
                string.IsNullOrWhiteSpace(label) ? fallbackLabel : $"{fallbackLabel} {label}",
                x,
                z,
                kind,
                MapPoiSources.More);
        }
    }

    private static string GetSpawnKind(JsonElement spawn)
    {
        var categories = ReadStringArray(spawn, "categories");
        var sides = ReadStringArray(spawn, "sides");
        if (categories.Contains("boss", StringComparer.OrdinalIgnoreCase))
        {
            return "spawnBoss";
        }

        if (categories.Contains("sniper", StringComparer.OrdinalIgnoreCase))
        {
            return "spawnSniper";
        }

        if (categories.Contains("botpmc", StringComparer.OrdinalIgnoreCase))
        {
            return "spawnRogue";
        }

        if (sides.Contains("pmc", StringComparer.OrdinalIgnoreCase) && !sides.Contains("scav", StringComparer.OrdinalIgnoreCase))
        {
            return "spawnPmc";
        }

        return "spawnScav";
    }

    private static bool TryReadPosition(JsonElement element, out double x, out double z)
    {
        if (element.TryGetProperty("position", out var positionNode))
        {
            return TryReadPositionValue(positionNode, out x, out z);
        }

        return TryReadPositionValue(element, out x, out z);
    }

    private static bool TryReadPositionY(JsonElement element, out double y)
    {
        y = 0;
        if (element.TryGetProperty("position", out var positionNode) &&
            positionNode.ValueKind == JsonValueKind.Object)
        {
            return TryReadNumber(positionNode, "y", out y);
        }

        return element.ValueKind == JsonValueKind.Object && TryReadNumber(element, "y", out y);
    }

    private static bool TryReadHeightRange(JsonElement data, out double minY, out double maxY)
    {
        minY = maxY = 0;
        if (!data.TryGetProperty("heightRange", out var rangeNode) ||
            rangeNode.ValueKind != JsonValueKind.Array ||
            rangeNode.GetArrayLength() < 2 ||
            !TryReadNumber(rangeNode[0], out minY) ||
            !TryReadNumber(rangeNode[1], out maxY))
        {
            return false;
        }

        if (minY > maxY)
        {
            (minY, maxY) = (maxY, minY);
        }

        return true;
    }

    private static bool TryReadPositionValue(JsonElement value, out double x, out double z)
    {
        x = z = 0;
        if (value.ValueKind == JsonValueKind.Object)
        {
            return TryReadNumber(value, "x", out x) &&
                TryReadNumber(value, "z", out z);
        }

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2)
        {
            return TryReadNumber(value[0], out x) &&
                TryReadNumber(value[1], out z);
        }

        return false;
    }

    private static bool TryReadNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var node) ||
            node.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = node.GetDouble();
        return double.IsFinite(value);
    }

    private static bool TryReadNumber(JsonElement node, out double value)
    {
        value = 0;
        if (node.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = node.GetDouble();
        return double.IsFinite(value);
    }

    private static bool TryReadBounds(JsonElement data, out double x0, out double z0, out double x1, out double z1)
    {
        x0 = z0 = x1 = z1 = 0;
        if (!data.TryGetProperty("bounds", out var boundsNode) ||
            boundsNode.ValueKind != JsonValueKind.Array ||
            boundsNode.GetArrayLength() < 2)
        {
            return false;
        }

        var b0 = boundsNode[0];
        var b1 = boundsNode[1];
        if (b0.ValueKind != JsonValueKind.Array ||
            b1.ValueKind != JsonValueKind.Array ||
            b0.GetArrayLength() < 2 ||
            b1.GetArrayLength() < 2)
        {
            return false;
        }

        x0 = b0[0].GetDouble();
        z0 = b0[1].GetDouble();
        x1 = b1[0].GetDouble();
        z1 = b1[1].GetDouble();
        return true;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()?.Trim() ?? ""
            : "";
    }

    private static string ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private static string ReadNestedFirstString(JsonElement element, params string[] paths)
    {
        foreach (var path in paths)
        {
            var current = element;
            var found = true;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(part, out current))
                {
                    found = false;
                    break;
                }
            }

            if (found && current.ValueKind == JsonValueKind.String)
            {
                var text = current.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return "";
    }

    private static string GetExtractIconName(JsonElement extract)
    {
        var faction = ReadString(extract, "faction").ToLowerInvariant();
        return faction switch
        {
            "pmc" => "extract_pmc",
            "scav" => "extract_scav",
            "shared" => "extract_shared",
            _ => "extract_shared"
        };
    }

    private static string GetExtractDisplayName(JsonElement extract)
    {
        var baseName = ReadFirstString(extract, "name", "id");
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "未知撤离点";
        }

        var faction = ReadString(extract, "faction").ToLowerInvariant();
        var factionLabel = faction switch
        {
            "pmc" => "PMC",
            "scav" => "Scav",
            _ => "共享"
        };

        var tags = new List<string> { factionLabel };
        if (HasArrayItems(extract, "switches"))
        {
            tags.Add("需拉闸");
        }

        if (HasArrayItems(extract, "requirements"))
        {
            tags.Add("有条件");
        }

        return $"{baseName}（{string.Join("，", tags)}）";
    }

    private static string GetTransitDisplayName(JsonElement transit)
    {
        var description = ReadString(transit, "description");
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var name = ReadString(transit, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var targetName = ReadNestedFirstString(transit, "map.name", "map.normalizedName", "map.id");
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            return $"前往 {targetName}";
        }

        var id = ReadString(transit, "id");
        return string.IsNullOrWhiteSpace(id) ? "转移点" : id;
    }

    private static bool HasArrayItems(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var node) &&
            node.ValueKind == JsonValueKind.Array &&
            node.GetArrayLength() > 0;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node) ||
            node.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return node.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }
}
