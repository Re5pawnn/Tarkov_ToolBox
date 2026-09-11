using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovMapLocator.Modules.TaskTracking;
using TarkovMapLocator.Modules.TaskTracking.Models;

namespace TarkovMapLocator.Modules.TaskTracking.Services;

public static class KaedeoriTaskDetailService
{
    private const string DetailEndpoint = "https://member.kaedeori.com/api/tarkov/betaTask/detail";
    private const string DocumentEndpoint = "https://member.kaedeori.com/api/tarkov/docs/detail";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumCachedDetails = 32;
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<TaskTrackingDetailLoadResult>> Pending = new(StringComparer.Ordinal);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, TaskTrackingDetailLoadResult> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LinkedListNode<string>> CacheNodes = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> CacheOrder = [];

    public static Task<TaskTrackingDetailLoadResult> GetAsync(string sourceTaskId, string mode)
    {
        var normalized = sourceTaskId.Trim();
        if (normalized.Length == 0)
            return Task.FromResult(new TaskTrackingDetailLoadResult(null, "该任务缺少详情标识。"));
        var normalizedMode = NormalizeMode(mode);
        var cacheKey = $"{normalizedMode}\n{normalized}";
        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                var node = CacheNodes[cacheKey];
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                return Task.FromResult(cached);
            }
        }
        return Pending.GetOrAdd(cacheKey, _ => LoadAndCacheAsync(cacheKey, normalized, normalizedMode));
    }

    private static async Task<TaskTrackingDetailLoadResult> LoadAndCacheAsync(
        string cacheKey,
        string sourceTaskId,
        string mode)
    {
        try
        {
            var result = await LoadAsync(sourceTaskId, mode).ConfigureAwait(false);
            if (result.IsAvailable)
            {
                lock (CacheGate)
                {
                    Cache[cacheKey] = result;
                    if (CacheNodes.TryGetValue(cacheKey, out var existing)) CacheOrder.Remove(existing);
                    CacheNodes[cacheKey] = CacheOrder.AddFirst(cacheKey);
                    while (Cache.Count > MaximumCachedDetails && CacheOrder.Last is { } oldest)
                    {
                        CacheOrder.RemoveLast();
                        CacheNodes.Remove(oldest.Value);
                        Cache.Remove(oldest.Value);
                    }
                }
            }
            return result;
        }
        finally
        {
            Pending.TryRemove(cacheKey, out _);
        }
    }

    private static async Task<TaskTrackingDetailLoadResult> LoadAsync(string sourceTaskId, string mode)
    {
        try
        {
            var detailUrl = $"{DetailEndpoint}?id={Uri.EscapeDataString(sourceTaskId)}&requireItems=true&requireCrafts=true&lang=zh&gameMode={Uri.EscapeDataString(mode)}";
            using var document = await GetJsonAsync(detailUrl).ConfigureAwait(false);
            var responseData = document.RootElement.GetProperty("data");
            var task = responseData.GetProperty("data");
            var items = ReadItems(responseData);
            var relatedNames = ReadRelatedNames(responseData);

            var docKey = ReadString(task, "taskGuide", "docKey");
            var guide = string.IsNullOrWhiteSpace(docKey)
                ? ""
                : await LoadGuideAsync(docKey).ConfigureAwait(false);
            var detail = new TaskTrackingDetail(
                ReadString(task, "id") is { Length: > 0 } id ? id : sourceTaskId,
                ReadString(task, "name"),
                ReadString(task, "betaTrader") is { Length: > 0 } trader ? trader : ReadString(task, "trader"),
                NormalizeMapName(ReadString(task, "map")),
                ReadString(task, "description"),
                ReadString(task, "taskImageLink"),
                ReadInt(task, "experience"),
                ReadObjectives(task),
                ReadRelatedTasks(task, relatedNames, "previousTaskIds", "taskRequirements"),
                ReadRelatedTasks(task, relatedNames, "nextTaskIds", "taskForwards"),
                ReadItemRewards(task, items),
                ReadOfferUnlocks(task, items),
                ReadOtherRewards(task),
                ReadNeededKeys(task, items),
                ReadString(task, "rewardText"),
                guide,
                ReadString(task, "taskGuide", "videoLink"));
            return new TaskTrackingDetailLoadResult(detail, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            TaskTrackingRuntime.Warning("读取枫织梦境任务详情失败", $"任务: {sourceTaskId}\n{exception.Message}");
            return new TaskTrackingDetailLoadResult(null, "任务详情读取失败，请检查网络后重试。");
        }
    }

    private static async Task<string> LoadGuideAsync(string docKey)
    {
        try
        {
            var url = $"{DocumentEndpoint}?docKey={Uri.EscapeDataString(docKey)}&lang=zh";
            using var document = await GetJsonAsync(url).ConfigureAwait(false);
            var data = document.RootElement.GetProperty("data");
            var text = ReadString(data, "text");
            if (text.Length == 0 && data.TryGetProperty("content", out var content))
                text = ReadString(content, "text");
            return text.Replace(
                "https://docs.tiltysola.com/api/attachments.redirect?id=",
                "https://member.kaedeori.com/api/tarkov/docs/attachment?id=",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            TaskTrackingRuntime.Warning("读取任务攻略正文失败", $"文档: {docKey}\n{exception.Message}");
            return "";
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("任务详情响应超过大小限制。");
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaximumResponseBytes)
            throw new InvalidDataException("任务详情响应为空或超过大小限制。");
        return JsonDocument.Parse(bytes);
    }

    private static Dictionary<string, ItemInfo> ReadItems(JsonElement responseData)
    {
        var result = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
        if (!responseData.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in items.EnumerateObject())
        {
            var value = property.Value;
            result[property.Name] = new ItemInfo(
                ReadString(value, "name") is { Length: > 0 } name ? name : property.Name,
                ReadString(value, "iconLink"));
        }
        return result;
    }

    private static Dictionary<string, string> ReadRelatedNames(JsonElement responseData)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!responseData.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in tasks.EnumerateObject())
            result[property.Name] = ReadString(property.Value, "name") is { Length: > 0 } name ? name : property.Name;
        return result;
    }

    private static IReadOnlyList<TaskTrackingObjective> ReadObjectives(JsonElement task)
    {
        var result = new List<TaskTrackingObjective>();
        if (!task.TryGetProperty("objectives", out var objectives) || objectives.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var objective in objectives.EnumerateArray())
        {
            var description = ReadString(objective, "description");
            if (description.Length == 0) continue;
            result.Add(new TaskTrackingObjective(
                description,
                ReadDouble(objective, "count"),
                ReadBool(objective, "optional")));
        }
        return result;
    }

    private static IReadOnlyList<TaskTrackingRelatedTask> ReadRelatedTasks(
        JsonElement task,
        IReadOnlyDictionary<string, string> names,
        string idProperty,
        string fallbackProperty)
    {
        var result = new List<TaskTrackingRelatedTask>();
        if (task.TryGetProperty(idProperty, out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var idElement in ids.EnumerateArray())
            {
                var id = idElement.GetString()?.Trim() ?? "";
                if (id.Length > 0)
                    result.Add(new TaskTrackingRelatedTask(id, names.GetValueOrDefault(id, id)));
            }
        }

        if (result.Count == 0 && task.TryGetProperty(fallbackProperty, out var fallback) && fallback.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in fallback.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var stringId = entry.GetString()?.Trim() ?? "";
                    if (stringId.Length > 0) result.Add(new TaskTrackingRelatedTask(stringId, names.GetValueOrDefault(stringId, stringId)));
                    continue;
                }
                if (!entry.TryGetProperty("task", out var related)) continue;
                var relatedId = ReadString(related, "id");
                if (relatedId.Length > 0)
                    result.Add(new TaskTrackingRelatedTask(relatedId, ReadString(related, "name") is { Length: > 0 } name ? name : names.GetValueOrDefault(relatedId, relatedId)));
            }
        }
        return result.DistinctBy(related => related.SourceTaskId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<TaskTrackingRewardItem> ReadItemRewards(
        JsonElement task,
        IReadOnlyDictionary<string, ItemInfo> items)
    {
        var result = new List<TaskTrackingRewardItem>();
        if (!TryGetRewardArray(task, "items", out var rewards)) return result;
        foreach (var reward in rewards.EnumerateArray())
        {
            var id = ReadString(reward, "item", "id");
            if (id.Length == 0) continue;
            var info = items.GetValueOrDefault(id, new ItemInfo(id, ""));
            result.Add(new TaskTrackingRewardItem(id, info.Name, ReadDouble(reward, "count"), info.IconUrl));
        }
        return result;
    }

    private static IReadOnlyList<TaskTrackingOfferUnlock> ReadOfferUnlocks(
        JsonElement task,
        IReadOnlyDictionary<string, ItemInfo> items)
    {
        var result = new List<TaskTrackingOfferUnlock>();
        if (!TryGetRewardArray(task, "offerUnlock", out var rewards)) return result;
        foreach (var reward in rewards.EnumerateArray())
        {
            var id = ReadString(reward, "item", "id");
            if (id.Length == 0) continue;
            var info = items.GetValueOrDefault(id, new ItemInfo(id, ""));
            result.Add(new TaskTrackingOfferUnlock(
                id,
                info.Name,
                ReadString(reward, "trader", "id"),
                ReadInt(reward, "level"),
                info.IconUrl));
        }
        return result;
    }

    private static IReadOnlyList<string> ReadOtherRewards(JsonElement task)
    {
        var result = new List<string>();
        if (ReadInt(task, "experience") is > 0 and var experience)
            result.Add($"经验：{experience:N0}");
        if (!task.TryGetProperty("finishRewards", out var rewards) || rewards.ValueKind != JsonValueKind.Object)
            return result;
        AddRewardValues("traderStanding", "商人好感");
        AddRewardValues("skillLevelReward", "技能奖励");
        AddRewardValues("traderUnlock", "解锁商人");
        AddRewardValues("craftUnlock", "解锁制作");
        if (rewards.TryGetProperty("text", out var textRewards) && textRewards.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in textRewards.EnumerateArray())
            {
                var value = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetRawText();
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
            }
        }
        return result.Distinct(StringComparer.CurrentCulture).ToArray();

        void AddRewardValues(string property, string label)
        {
            if (!rewards.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return;
            foreach (var value in values.EnumerateArray())
            {
                var name = ReadString(value, "trader", "id");
                if (name.Length == 0) name = ReadString(value, "skill", "name");
                if (name.Length == 0) name = ReadString(value, "item", "id");
                var amount = ReadDouble(value, "standing");
                if (amount == 0) amount = ReadDouble(value, "level");
                if (amount == 0) amount = ReadDouble(value, "count");
                var suffix = amount == 0 ? "" : $" {amount.ToString("0.##", CultureInfo.CurrentCulture)}";
                result.Add($"{label}：{name}{suffix}".TrimEnd('：'));
            }
        }
    }

    private static IReadOnlyList<TaskTrackingRewardItem> ReadNeededKeys(
        JsonElement task,
        IReadOnlyDictionary<string, ItemInfo> items)
    {
        var result = new List<TaskTrackingRewardItem>();
        if (!task.TryGetProperty("neededKeys", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) continue;
            foreach (var key in keys.EnumerateArray())
            {
                var id = ReadString(key, "id");
                if (id.Length == 0) continue;
                var info = items.GetValueOrDefault(id, new ItemInfo(id, ""));
                result.Add(new TaskTrackingRewardItem(id, info.Name, 1, info.IconUrl));
            }
        }
        return result.DistinctBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryGetRewardArray(JsonElement task, string property, out JsonElement array)
    {
        if (task.TryGetProperty("finishRewards", out var rewards) &&
            rewards.ValueKind == JsonValueKind.Object &&
            rewards.TryGetProperty(property, out array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        array = default;
        return false;
    }

    private static string ReadString(JsonElement element, params string[] path)
    {
        foreach (var property in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out element)) return "";
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? "" : "";
    }

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : 0;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string NormalizeMapName(string value) =>
        string.Equals(value.Trim(), "Any", StringComparison.OrdinalIgnoreCase) ? "任意地图" : value;

    private static string NormalizeMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "pvp" => "pvp",
        "season" => "season",
        _ => "pve"
    };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 4,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/task-detail");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed record ItemInfo(string Name, string IconUrl);
}
