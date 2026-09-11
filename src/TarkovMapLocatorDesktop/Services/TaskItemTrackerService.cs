using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Task-item and hideout-requirement tracker. Requirements are assembled from
/// json.tarkov.dev static snapshots; user quantities and source completion
/// remain local and preserve the original tracker state format.
/// </summary>
public static class TaskItemTrackerService
{
    private const string JsonApiBaseUrl = "https://json.tarkov.dev";
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly object StateGate = new();
    private static readonly object CacheGate = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly SemaphoreSlim JsonResourceGate = new(2, 2);
    private static readonly object AutomaticRefreshGate = new();
    private static Task? _automaticRefreshTask;
    private static TrackerCacheFile? _memoryCache;
    private static DateTime _memoryCacheStamp = DateTime.MinValue;
    private static TrackerStateFile? _memoryState;
    private static DateTime _memoryStateStamp = DateTime.MinValue;

    private static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMapLocator");
    private static readonly string CachePath = Path.Combine(AppDataDirectory, "item-tracker-cache.json");
    private static readonly string StatePath = Path.Combine(AppDataDirectory, "item-tracker-state.json");

    public static TaskTrackerCacheStatus GetCacheStatus()
    {
        var cache = ReadCache();
        // A fresh pre-migration cache remains displayable, but it must be refreshed
        // once so the application moves to the supported JSON source.
        return new TaskTrackerCacheStatus(
            cache is not null,
            cache is not null && (IsStale(cache) || !IsJsonApiCache(cache)));
    }

    public static TaskTrackerLoadResult Load(string mode, string query, bool includeTasks, bool includeHideout, bool showCompleted)
    {
        mode = NormalizeMode(mode);
        var cache = ReadCache();
        if (cache is null)
            return new TaskTrackerLoadResult([], null, "未找到任务物品缓存。请点击“刷新清单”获取数据。", false, 0, 0, 0, 0);

        var state = ReadState();
        var collected = state.Items.TryGetValue(mode, out var modeItems) ? modeItems : [];
        var completedSources = state.CompletedSources.TryGetValue(mode, out var modeSources) ? modeSources : [];
        var hideoutLevels = state.HideoutLevels.TryGetValue(mode, out var modeHideoutLevels) ? modeHideoutLevels : [];
        var marketById = MarketPriceService.GetItemIndex(mode);
        var groups = new Dictionary<string, TrackerItemBuilder>(StringComparer.Ordinal);

        foreach (var requirement in cache.Requirements.TryGetValue(mode, out var rows) ? rows : [])
        {
            if (requirement.SourceType == "task" && !includeTasks || requirement.SourceType == "hideout" && !includeHideout) continue;
            if (IsBuiltHideoutRequirement(requirement, hideoutLevels)) continue;
            if (string.IsNullOrWhiteSpace(requirement.ItemId)) continue;
            var key = SourceKey(requirement);
            var completed = completedSources.TryGetValue(key, out var sourceCompleted) && sourceCompleted;
            if (!groups.TryGetValue(requirement.ItemId, out var item))
            {
                marketById.TryGetValue(requirement.ItemId, out var marketItem);
                item = new TrackerItemBuilder(
                    requirement.ItemId,
                    marketItem?.NameZh is { Length: > 0 } nameZh ? nameZh : requirement.Item.Name,
                    marketItem?.ShortNameZh is { Length: > 0 } shortNameZh ? shortNameZh : requirement.Item.ShortName,
                    marketItem?.GridImageLink is { Length: > 0 } gridImage ? gridImage : requirement.Item.GridImageLink ?? requirement.Item.IconLink ?? "",
                    collected.TryGetValue(requirement.ItemId, out var have) ? Math.Max(0, have) : 0,
                    marketItem?.FleaPrice,
                    marketItem?.Avg24hPrice,
                    requirement.Choice,
                    requirement.Item.Choices?.Select(choice => choice.ShortName is { Length: > 0 } shortName ? shortName : choice.Name).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? []);
                groups.Add(requirement.ItemId, item);
            }

            var count = Math.Max(0, requirement.Count);
            if (requirement.FoundInRaid) item.FoundInRaidRequired += count;
            if (completed)
            {
                item.CompletedRequired += count;
                if (requirement.SourceType == "task") item.CompletedTaskRequired += count;
                if (requirement.SourceType == "hideout") item.CompletedHideoutRequired += count;
            }
            else
            {
                item.Required += count;
                if (requirement.SourceType == "task") item.TaskRequired += count;
                if (requirement.SourceType == "hideout") item.HideoutRequired += count;
            }
            if (item.Sources.Count < 80)
                item.Sources.Add(new TrackerRequirementSource(key, requirement.SourceType, requirement.SourceName, requirement.SourceDetail, count, requirement.FoundInRaid, requirement.Trader, completed, requirement.Choice));
        }

        var term = query.Trim();
        var visible = groups.Values.Select(builder => builder.ToItem())
            .Where(item => string.IsNullOrWhiteSpace(term) || Matches(item, term))
            .Where(item => showCompleted || item.Remaining > 0)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Take(1000)
            .ToArray();
        return new TaskTrackerLoadResult(
            visible,
            cache.UpdatedAt,
            null,
            IsStale(cache),
            visible.Sum(item => (long)item.Required),
            visible.Sum(item => (long)item.Remaining),
            visible.Sum(item => item.EstimatedCost ?? 0L),
            state.UpdatedAt ?? 0);
    }

    /// <summary>
    /// A missing, stale, or legacy-source cache refreshes in the background. A JSON
    /// cache is validated by ETag when the user explicitly refreshes it.
    /// </summary>
    public static Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        var cache = ReadCache();
        if (cache is not null && !IsStale(cache) && IsJsonApiCache(cache)) return Task.CompletedTask;

        lock (AutomaticRefreshGate)
        {
            if (_automaticRefreshTask is { IsCompleted: false }) return _automaticRefreshTask;
            _automaticRefreshTask = RefreshAsync(cancellationToken);
            return _automaticRefreshTask;
        }
    }

    public static void SetItemCount(string mode, string itemId, int have)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > 160) return;
        mode = NormalizeMode(mode);
        lock (StateGate)
        {
            var state = ReadState();
            if (!state.Items.TryGetValue(mode, out var items)) state.Items[mode] = items = [];
            if (have > 0) items[itemId] = have;
            else items.Remove(itemId);
            state.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteState(state);
        }
    }

    public static void SetSourceCompleted(string mode, string sourceKey, bool completed)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 500) return;
        mode = NormalizeMode(mode);
        lock (StateGate)
        {
            var state = ReadState();
            if (!state.CompletedSources.TryGetValue(mode, out var sources)) state.CompletedSources[mode] = sources = [];
            if (completed) sources[sourceKey] = true;
            else sources.Remove(sourceKey);
            state.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteState(state);
        }
    }

    public static IReadOnlyList<HideoutStationLevel> GetHideoutStations(string mode)
    {
        mode = NormalizeMode(mode);
        var cache = ReadCache();
        if (cache is null || !cache.Requirements.TryGetValue(mode, out var requirements)) return [];
        var state = ReadState();
        var currentLevels = state.HideoutLevels.TryGetValue(mode, out var levels) ? levels : [];

        return requirements
            .Where(requirement => string.Equals(requirement.SourceType, "hideout", StringComparison.Ordinal) &&
                                  requirement.Level is > 0 &&
                                  !string.IsNullOrWhiteSpace(HideoutStationKey(requirement)))
            .GroupBy(HideoutStationKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var maxLevel = group.Max(requirement => requirement.Level ?? 0);
                var currentLevel = currentLevels.TryGetValue(group.Key, out var savedLevel)
                    ? Math.Clamp(savedLevel, 0, maxLevel)
                    : 0;
                return new HideoutStationLevel(group.Key, first.SourceName, currentLevel, maxLevel);
            })
            .OrderBy(station => station.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static void SetHideoutLevels(string mode, IReadOnlyDictionary<string, int> levels)
    {
        mode = NormalizeMode(mode);
        var maximumLevels = GetHideoutStations(mode).ToDictionary(station => station.Id, station => station.MaxLevel, StringComparer.Ordinal);
        lock (StateGate)
        {
            var state = ReadState();
            var normalized = levels
                .Where(pair => maximumLevels.TryGetValue(pair.Key, out var maximum) && pair.Value > 0 && maximum > 0)
                .ToDictionary(
                    pair => pair.Key,
                    pair => Math.Clamp(pair.Value, 0, maximumLevels[pair.Key]),
                    StringComparer.Ordinal);
            if (normalized.Count > 0) state.HideoutLevels[mode] = normalized;
            else state.HideoutLevels.Remove(mode);
            state.SchemaVersion = 3;
            state.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteState(state);
        }
    }

    public static async Task<TaskTrackerLoadResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = ReadCache();
            var refreshed = await RefreshJsonApiAsync(cache, cancellationToken).ConfigureAwait(false);
            WriteCache(refreshed);
            return Load("pvp", "", true, true, false);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<TrackerCacheFile> RefreshJsonApiAsync(TrackerCacheFile? cached, CancellationToken cancellationToken)
    {
        var etags = CopyEtags(cached?.JsonApiEtags ?? new Dictionary<string, string>());
        var cachedPvp = cached?.Requirements.GetValueOrDefault("pvp") ?? [];
        var cachedPve = cached?.Requirements.GetValueOrDefault("pve") ?? [];
        var cacheIsJson = cached is not null && IsJsonApiCache(cached);
        var results = await Task.WhenAll(
            RefreshModeAsync("regular", "pvp", cachedPvp, cacheIsJson, etags, cancellationToken),
            RefreshModeAsync("pve", "pve", cachedPve, cacheIsJson, etags, cancellationToken)).ConfigureAwait(false);
        if (results.All(result => result.Requirements.Count == 0))
            throw new InvalidOperationException("任务清单服务没有返回可用数据，且没有本地缓存。");

        var requirements = new Dictionary<string, List<TrackerRequirement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
            if (result.Requirements.Count > 0)
                requirements[result.Mode] = result.Requirements.Select(CloneRequirement).ToList();
        var successfulResources = results.Select(result => result.Resources).OfType<JsonApiModeResources>().ToArray();

        return new TrackerCacheFile
        {
            SchemaVersion = 2,
            UpdatedAtUnix = results.All(result => result.Resources is not null)
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                : cached?.UpdatedAtUnix ?? 0,
            Source = JsonApiBaseUrl,
            PatchSource = "embedded-miaomiao-toolbox",
            JsonApiEtags = MergeEtags(etags, successfulResources).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            Requirements = requirements
        };
    }

    private static async Task<ModeRefreshResult> RefreshModeAsync(
        string gameMode,
        string mode,
        IReadOnlyList<TrackerRequirement> cachedRequirements,
        bool cacheIsJson,
        IReadOnlyDictionary<string, string> etags,
        CancellationToken cancellationToken)
    {
        try
        {
            var resources = await FetchModeResourcesAsync(gameMode, etags, cancellationToken).ConfigureAwait(false);
            if (cacheIsJson && cachedRequirements.Count > 0 && !resources.RequiresRebuild)
                return new ModeRefreshResult(mode, cachedRequirements, resources);

            resources = await EnsureModePayloadsAsync(resources, cancellationToken).ConfigureAwait(false);
            var traderNames = await FetchTraderNamesAsync(cancellationToken).ConfigureAwait(false);
            var requirements = MergePatches(
                BuildJsonApiRequirements(resources, traderNames),
                ReadEmbeddedPatches(mode),
                cachedRequirements).ToArray();
            if (requirements.Length == 0) throw new InvalidOperationException("接口返回空数据");
            return new ModeRefreshResult(mode, requirements, resources);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RuntimeLogService.Warning("任务清单", $"{mode.ToUpperInvariant()} 数据刷新失败，保留该模式缓存", exception.Message);
            return new ModeRefreshResult(mode, cachedRequirements, null);
        }
    }

    private static async Task<JsonApiModeResources> FetchModeResourcesAsync(
        string gameMode,
        IReadOnlyDictionary<string, string> etags,
        CancellationToken cancellationToken)
    {
        var tasks = CreateResource(gameMode, "tasks");
        var tasksZh = CreateResource(gameMode, "tasks_zh");
        var hideout = CreateResource(gameMode, "hideout");
        var hideoutZh = CreateResource(gameMode, "hideout_zh");
        var items = CreateResource(gameMode, "items");
        var itemsZh = CreateResource(gameMode, "items_zh");
        var requests = new[]
        {
            FetchJsonResourceAsync(tasks.CacheKey, tasks.Url, TryGetEtag(etags, tasks.CacheKey), cancellationToken),
            FetchJsonResourceAsync(tasksZh.CacheKey, tasksZh.Url, TryGetEtag(etags, tasksZh.CacheKey), cancellationToken),
            FetchJsonResourceAsync(hideout.CacheKey, hideout.Url, TryGetEtag(etags, hideout.CacheKey), cancellationToken),
            FetchJsonResourceAsync(hideoutZh.CacheKey, hideoutZh.Url, TryGetEtag(etags, hideoutZh.CacheKey), cancellationToken),
            FetchJsonResourceAsync(items.CacheKey, items.Url, TryGetEtag(etags, items.CacheKey), cancellationToken),
            FetchJsonResourceAsync(itemsZh.CacheKey, itemsZh.Url, TryGetEtag(etags, itemsZh.CacheKey), cancellationToken)
        };
        var result = await Task.WhenAll(requests).ConfigureAwait(false);
        return new JsonApiModeResources(gameMode, result[0], result[1], result[2], result[3], result[4], result[5]);
    }

    private static async Task<JsonApiModeResources> EnsureModePayloadsAsync(JsonApiModeResources resources, CancellationToken cancellationToken)
    {
        var tasksTask = EnsurePayloadAsync(resources.Tasks, cancellationToken);
        var tasksZhTask = EnsurePayloadAsync(resources.TasksZh, cancellationToken);
        var hideoutTask = EnsurePayloadAsync(resources.Hideout, cancellationToken);
        var hideoutZhTask = EnsurePayloadAsync(resources.HideoutZh, cancellationToken);
        var itemsTask = EnsurePayloadAsync(resources.Items, cancellationToken);
        var itemsZhTask = EnsurePayloadAsync(resources.ItemsZh, cancellationToken);
        await Task.WhenAll(tasksTask, tasksZhTask, hideoutTask, hideoutZhTask, itemsTask, itemsZhTask).ConfigureAwait(false);
        return resources with
        {
            Tasks = tasksTask.Result,
            TasksZh = tasksZhTask.Result,
            Hideout = hideoutTask.Result,
            HideoutZh = hideoutZhTask.Result,
            Items = itemsTask.Result,
            ItemsZh = itemsZhTask.Result
        };
    }

    private static Task<JsonApiResource> EnsurePayloadAsync(JsonApiResource resource, CancellationToken cancellationToken) =>
        resource.Payload is { Length: > 0 }
            ? Task.FromResult(resource)
            : FetchJsonResourceAsync(resource.CacheKey, resource.Url, null, cancellationToken);

    private static async Task<IReadOnlyDictionary<string, string>> FetchTraderNamesAsync(CancellationToken cancellationToken)
    {
        // Trader data is small and only needed when a task/hideout snapshot changed.
        var traders = CreateResource("regular", "traders");
        var tradersZh = CreateResource("regular", "traders_zh");
        var tradersTask = FetchJsonResourceAsync(traders.CacheKey, traders.Url, null, cancellationToken);
        var tradersZhTask = FetchJsonResourceAsync(tradersZh.CacheKey, tradersZh.Url, null, cancellationToken);
        await Task.WhenAll(tradersTask, tradersZhTask).ConfigureAwait(false);
        return ParseJsonApiTraderNames(tradersTask.Result.RequirePayload(), tradersZhTask.Result.RequirePayload());
    }

    private static async Task<JsonApiResource> FetchJsonResourceAsync(
        string cacheKey,
        string url,
        string? etag,
        CancellationToken cancellationToken)
    {
        await JsonResourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await TarkovDevRequestPolicy.SendAsync(
                Client,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url)
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                    };
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var entityTag))
                        request.Headers.IfNoneMatch.Add(entityTag);
                    return request;
                },
                $"任务 JSON 资源 {cacheKey}",
                cancellationToken,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new JsonApiResource(cacheKey, url, null, etag, true);
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{ReadServerHint(content)}", null, response.StatusCode);
            }

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (payload.Length == 0) throw new InvalidOperationException($"任务 JSON 资源 {cacheKey} 返回为空。");
            return new JsonApiResource(cacheKey, url, payload, response.Headers.ETag?.ToString() ?? etag, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"读取 {cacheKey} 失败：{Explain(exception)}", exception);
        }
        finally
        {
            JsonResourceGate.Release();
        }
    }

    internal static IReadOnlyList<TrackerRequirement> BuildJsonApiRequirementsForTest(
        byte[] tasksPayload,
        byte[] tasksZhPayload,
        byte[] hideoutPayload,
        byte[] hideoutZhPayload,
        byte[] itemsPayload,
        byte[] itemsZhPayload,
        byte[] tradersPayload,
        byte[] tradersZhPayload)
    {
        var resources = new JsonApiModeResources(
            "test",
            new JsonApiResource("tasks", "", tasksPayload, null, false),
            new JsonApiResource("tasks_zh", "", tasksZhPayload, null, false),
            new JsonApiResource("hideout", "", hideoutPayload, null, false),
            new JsonApiResource("hideout_zh", "", hideoutZhPayload, null, false),
            new JsonApiResource("items", "", itemsPayload, null, false),
            new JsonApiResource("items_zh", "", itemsZhPayload, null, false));
        return BuildJsonApiRequirements(resources, ParseJsonApiTraderNames(tradersPayload, tradersZhPayload));
    }

    private static List<TrackerRequirement> BuildJsonApiRequirements(
        JsonApiModeResources resources,
        IReadOnlyDictionary<string, string> traderNames)
    {
        using var tasksDocument = JsonDocument.Parse(resources.Tasks.RequirePayload());
        using var tasksZhDocument = JsonDocument.Parse(resources.TasksZh.RequirePayload());
        using var hideoutDocument = JsonDocument.Parse(resources.Hideout.RequirePayload());
        using var hideoutZhDocument = JsonDocument.Parse(resources.HideoutZh.RequirePayload());
        using var itemsDocument = JsonDocument.Parse(resources.Items.RequirePayload());
        using var itemsZhDocument = JsonDocument.Parse(resources.ItemsZh.RequirePayload());

        var taskTranslations = ReadTranslationMap(tasksZhDocument.RootElement);
        var hideoutTranslations = ReadTranslationMap(hideoutZhDocument.RootElement);
        var itemTranslations = ReadTranslationMap(itemsZhDocument.RootElement);
        var itemStubs = ParseJsonApiItems(itemsDocument.RootElement, itemTranslations);
        var taskRows = CollectJsonApiTaskRequirements(tasksDocument.RootElement, taskTranslations, itemStubs, traderNames);
        var hideoutRows = CollectJsonApiHideoutRequirements(hideoutDocument.RootElement, hideoutTranslations, itemStubs);
        taskRows.AddRange(hideoutRows);
        return taskRows;
    }

    private static IReadOnlyDictionary<string, TrackerItemStub> ParseJsonApiItems(
        JsonElement root,
        IReadOnlyDictionary<string, string> translations)
    {
        var data = RequireDataObject(root, "items");
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("任务 JSON 的 items 资源缺少 data.items 对象。");

        var result = new Dictionary<string, TrackerItemStub>(StringComparer.Ordinal);
        foreach (var property in items.EnumerateObject())
        {
            var item = property.Value;
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) id = property.Name;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var rawName = ReadString(item, "name");
            var rawShortName = ReadString(item, "shortName");
            var fallbackName = ReadString(item, "normalizedName");
            var name = Translate(rawName, translations, string.IsNullOrWhiteSpace(fallbackName) ? id : fallbackName);
            var shortName = Translate(rawShortName, translations, name);
            result[id] = new TrackerItemStub
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                ShortName = string.IsNullOrWhiteSpace(shortName) ? name : shortName,
                IconLink = ReadString(item, "iconLink"),
                GridImageLink = ReadString(item, "gridImageLink")
            };
        }

        return result;
    }

    private static List<TrackerRequirement> CollectJsonApiTaskRequirements(
        JsonElement root,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, TrackerItemStub> itemsById,
        IReadOnlyDictionary<string, string> traderNames)
    {
        var data = RequireDataObject(root, "tasks");
        if (!data.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("任务 JSON 的 tasks 资源缺少 data.tasks 对象。");

        var rows = new List<TrackerRequirement>();
        foreach (var taskProperty in tasks.EnumerateObject())
        {
            var task = taskProperty.Value;
            if (task.ValueKind != JsonValueKind.Object) continue;
            var taskId = ReadString(task, "id");
            if (string.IsNullOrWhiteSpace(taskId)) taskId = taskProperty.Name;
            if (string.IsNullOrWhiteSpace(taskId)) continue;
            if (!task.TryGetProperty("objectives", out var objectives) || objectives.ValueKind != JsonValueKind.Array) continue;

            var rawTraderId = ReadIdentifier(task, "trader");
            var trader = traderNames.TryGetValue(rawTraderId, out var traderName) ? traderName : rawTraderId;
            var taskName = Translate(ReadString(task, "name"), translations, ReadString(task, "normalizedName"));
            if (string.IsNullOrWhiteSpace(taskName)) taskName = taskId;
            var perTask = new Dictionary<string, TrackerRequirement>(StringComparer.Ordinal);
            foreach (var objective in objectives.EnumerateArray())
            {
                if (objective.ValueKind != JsonValueKind.Object || ReadBool(objective, "optional") || ReadInt(objective, "count") is not { } count || count <= 0)
                    continue;
                if (!objective.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                var choices = items.EnumerateArray()
                    .Select(value => ResolveItemStub(value, itemsById))
                    .Where(item => item is not null)
                    .Cast<TrackerItemStub>()
                    .GroupBy(item => item.Id, StringComparer.Ordinal)
                    .Select(group => CloneStub(group.First()))
                    .ToArray();
                if (choices.Length == 0) continue;

                var rawDetail = ReadString(objective, "description");
                var detail = Translate(rawDetail, translations, rawDetail);
                var common = new TrackerRequirement
                {
                    Count = count,
                    FoundInRaid = ReadBool(objective, "foundInRaid"),
                    SourceType = "task",
                    SourceId = taskId,
                    SourceName = taskName,
                    SourceDetail = detail,
                    Trader = trader,
                    Level = ReadInt(task, "minPlayerLevel")
                };
                if (choices.Length > 1)
                {
                    var objectiveId = Regex.Replace(ReadString(objective, "id"), "[^A-Za-z0-9_.:-]+", "-").Trim('-');
                    if (objectiveId.Length == 0) continue;
                    common.ItemId = $"choice:{objectiveId[..Math.Min(objectiveId.Length, 120)]}";
                    common.Item = new TrackerItemStub
                    {
                        Id = common.ItemId,
                        Name = string.IsNullOrWhiteSpace(detail) ? "任选物品" : detail,
                        ShortName = string.Join(" / ", choices.Take(6).Select(item => item.ShortName is { Length: > 0 } shortName ? shortName : item.Name)) + (choices.Length > 6 ? " ..." : ""),
                        Choices = choices.ToList()
                    };
                    common.Choice = true;
                    rows.Add(common);
                    continue;
                }

                common.Item = choices[0];
                common.ItemId = common.Item.Id;
                if (!perTask.TryGetValue(common.ItemId, out var existing)) perTask[common.ItemId] = common;
                else
                {
                    if (common.Count > existing.Count)
                    {
                        existing.Count = common.Count;
                        existing.SourceDetail = common.SourceDetail;
                    }
                    existing.FoundInRaid |= common.FoundInRaid;
                }
            }

            rows.AddRange(perTask.Values);
        }

        return rows;
    }

    private static List<TrackerRequirement> CollectJsonApiHideoutRequirements(
        JsonElement root,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, TrackerItemStub> itemsById)
    {
        var stations = RequireDataObject(root, "hideout");
        var rows = new List<TrackerRequirement>();
        foreach (var stationProperty in stations.EnumerateObject())
        {
            var station = stationProperty.Value;
            if (station.ValueKind != JsonValueKind.Object) continue;
            var stationId = ReadString(station, "id");
            if (string.IsNullOrWhiteSpace(stationId)) stationId = stationProperty.Name;
            if (string.IsNullOrWhiteSpace(stationId)) continue;
            var stationName = Translate(ReadString(station, "name"), translations, ReadString(station, "normalizedName"));
            if (string.IsNullOrWhiteSpace(stationName)) stationName = stationId;
            if (!station.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array) continue;

            foreach (var level in levels.EnumerateArray())
            {
                if (level.ValueKind != JsonValueKind.Object ||
                    !level.TryGetProperty("itemRequirements", out var requirements) ||
                    requirements.ValueKind != JsonValueKind.Array)
                    continue;
                var levelValue = ReadInt(level, "level");
                foreach (var requirement in requirements.EnumerateArray())
                {
                    if (requirement.ValueKind != JsonValueKind.Object || ReadInt(requirement, "count") is not { } count || count <= 0)
                        continue;
                    var stub = ResolveItemStub(requirement.TryGetProperty("item", out var item) ? item : default, itemsById);
                    if (stub is null) continue;
                    rows.Add(new TrackerRequirement
                    {
                        ItemId = stub.Id,
                        Item = stub,
                        Count = count,
                        FoundInRaid = IsFoundInRaid(requirement),
                        SourceType = "hideout",
                        SourceId = stationId,
                        SourceName = stationName,
                        SourceDetail = levelValue is { } value ? $"{stationName} {value}级" : stationName,
                        Station = stationName,
                        Level = levelValue
                    });
                }
            }
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, string> ParseJsonApiTraderNames(byte[] tradersPayload, byte[] translatorsPayload)
    {
        using var tradersDocument = JsonDocument.Parse(tradersPayload);
        using var translationsDocument = JsonDocument.Parse(translatorsPayload);
        var translations = ReadTranslationMap(translationsDocument.RootElement);
        var traders = RequireDataObject(tradersDocument.RootElement, "traders");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in traders.EnumerateObject())
        {
            var trader = property.Value;
            if (trader.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(trader, "id");
            if (string.IsNullOrWhiteSpace(id)) id = property.Name;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = Translate(ReadString(trader, "name"), translations, ReadString(trader, "normalizedName"));
            result[id] = string.IsNullOrWhiteSpace(name) ? id : name;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadTranslationMap(JsonElement root)
    {
        var data = RequireDataObject(root, "translation");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value)) result[property.Name] = value;
        }
        return result;
    }

    private static JsonElement RequireDataObject(JsonElement root, string resource)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"任务 JSON 的 {resource} 资源缺少 data 对象。");
        return data;
    }

    private static string Translate(string rawValue, IReadOnlyDictionary<string, string> translations, string fallback) =>
        !string.IsNullOrWhiteSpace(rawValue) && translations.TryGetValue(rawValue, out var translated) && !string.IsNullOrWhiteSpace(translated)
            ? translated
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : rawValue;

    private static TrackerItemStub? ResolveItemStub(JsonElement value, IReadOnlyDictionary<string, TrackerItemStub> itemsById)
    {
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ReadString(value, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return itemsById.TryGetValue(id, out var item)
            ? CloneStub(item)
            : new TrackerItemStub { Id = id, Name = id, ShortName = id };
    }

    private static string ReadIdentifier(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ReadString(value, "id");
    }

    private static List<TrackerRequirement> MergePatches(IEnumerable<TrackerRequirement> baseRows, IEnumerable<TrackerRequirement> embeddedPatches, IEnumerable<TrackerRequirement>? previousRows)
    {
        var merged = baseRows.Select(CloneRequirement).ToList();
        var seen = new HashSet<string>(merged.Where(row => row.SourceType == "task").Select(row => $"{row.SourceId}\u001f{row.ItemId}"), StringComparer.Ordinal);
        foreach (var patch in embeddedPatches.Concat(previousRows?.Where(row => row.PatchSource == "miaomiao-toolbox") ?? []))
        {
            var key = $"{patch.SourceId}\u001f{patch.ItemId}";
            if (string.IsNullOrWhiteSpace(patch.SourceId) || string.IsNullOrWhiteSpace(patch.ItemId) || !seen.Add(key)) continue;
            var copy = CloneRequirement(patch);
            copy.SourceType = "task";
            merged.Add(copy);
        }
        return merged;
    }

    private static IReadOnlyList<TrackerRequirement> ReadEmbeddedPatches(string mode)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith("OriginalTrackerPatches.json", StringComparison.Ordinal));
            if (resourceName is null) return [];
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var document = JsonDocument.Parse(stream!);
            var requirements = document.RootElement.GetProperty("requirements").GetProperty(mode);
            return JsonSerializer.Deserialize<List<TrackerRequirement>>(requirements.GetRawText(), JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }

    private static TrackerCacheFile? ReadCache()
    {
        lock (CacheGate)
        {
            var stamp = GetFileStamp(CachePath);
            if (_memoryCacheStamp == stamp) return _memoryCache;
            try
            {
                if (!File.Exists(CachePath)) return SetMemoryCache(null, stamp);
                using var stream = File.OpenRead(CachePath);
                var cache = JsonSerializer.Deserialize<TrackerCacheFile>(stream, JsonOptions);
                if (cache?.Requirements is not { Count: > 0 } requirements) return SetMemoryCache(null, stamp);
                var normalizedRequirements = new Dictionary<string, List<TrackerRequirement>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in requirements)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
                    var validRows = pair.Value
                        .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.ItemId))
                        .Select(NormalizeRequirement)
                        .ToList();
                    if (validRows.Count > 0) normalizedRequirements[pair.Key] = validRows;
                }
                if (normalizedRequirements.Count == 0) return SetMemoryCache(null, stamp);
                cache.Requirements = normalizedRequirements;
                cache.JsonApiEtags = CopyEtags(cache.JsonApiEtags ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                return SetMemoryCache(cache, stamp);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("任务清单", "读取任务物品缓存失败", $"文件: {CachePath}\n{exception}");
                return SetMemoryCache(null, stamp);
            }
        }
    }

    private static bool IsStale(TrackerCacheFile cache) =>
        cache.UpdatedAt == DateTimeOffset.MinValue || DateTimeOffset.Now - cache.UpdatedAt > TimeSpan.FromHours(24);

    private static bool IsJsonApiCache(TrackerCacheFile cache) =>
        cache.Source?.StartsWith(JsonApiBaseUrl, StringComparison.OrdinalIgnoreCase) == true;

    private static TrackerStateFile ReadState()
    {
        lock (StateGate)
        {
            var stamp = GetFileStamp(StatePath);
            if (_memoryState is not null && _memoryStateStamp == stamp) return CloneState(_memoryState);
            try
            {
                if (!File.Exists(StatePath)) return CloneState(SetMemoryState(new TrackerStateFile(), stamp));
                using var stream = File.OpenRead(StatePath);
                var state = JsonSerializer.Deserialize<TrackerStateFile>(stream, JsonOptions) ?? new TrackerStateFile();
                state.Items = NormalizeStateValues(state.Items);
                state.CompletedSources = NormalizeStateValues(state.CompletedSources);
                state.HideoutLevels = NormalizeStateValues(state.HideoutLevels);
                return CloneState(SetMemoryState(state, stamp));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("任务清单", "读取任务清单状态失败，已使用空状态", $"文件: {StatePath}\n{exception}");
                return CloneState(SetMemoryState(new TrackerStateFile(), stamp));
            }
        }
    }

    private static TrackerStateFile CloneState(TrackerStateFile state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        UpdatedAt = state.UpdatedAt,
        Items = state.Items.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, int>(pair.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase),
        CompletedSources = state.CompletedSources.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, bool>(pair.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase),
        HideoutLevels = state.HideoutLevels.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, int>(pair.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase)
    };

    private static void WriteCache(TrackerCacheFile cache)
    {
        lock (CacheGate)
        {
            SetMemoryCache(cache, GetFileStamp(CachePath));
            try
            {
                WriteJsonAtomic(CachePath, cache);
                SetMemoryCache(cache, GetFileStamp(CachePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("任务清单", "保存任务物品缓存失败，当前会话继续使用新数据", exception.Message);
            }
        }
    }

    private static void WriteState(TrackerStateFile state)
    {
        lock (StateGate)
        {
            SetMemoryState(state, GetFileStamp(StatePath));
            try
            {
                WriteJsonAtomic(StatePath, state);
                SetMemoryState(state, GetFileStamp(StatePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("任务清单", "保存任务清单状态失败，当前会话继续保留修改", exception.Message);
            }
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, value, JsonOptions);
            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("任务清单", "清理任务清单临时文件失败", exception.Message);
            }
        }
    }

    private static TrackerCacheFile? SetMemoryCache(TrackerCacheFile? cache, DateTime stamp)
    {
        _memoryCache = cache;
        _memoryCacheStamp = stamp;
        return cache;
    }

    private static TrackerStateFile SetMemoryState(TrackerStateFile state, DateTime stamp)
    {
        _memoryState = state;
        _memoryStateStamp = stamp;
        return state;
    }

    private static DateTime GetFileStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static string SourceKey(TrackerRequirement requirement) => string.Join("|", requirement.SourceType, requirement.SourceId, requirement.ItemId, requirement.Level?.ToString() ?? "", requirement.SourceName);
    private static string HideoutStationKey(TrackerRequirement requirement) =>
        !string.IsNullOrWhiteSpace(requirement.SourceId) ? requirement.SourceId : requirement.Station ?? requirement.SourceName;
    private static bool IsBuiltHideoutRequirement(TrackerRequirement requirement, IReadOnlyDictionary<string, int> levels) =>
        string.Equals(requirement.SourceType, "hideout", StringComparison.Ordinal) &&
        requirement.Level is { } requiredLevel &&
        levels.TryGetValue(HideoutStationKey(requirement), out var currentLevel) &&
        requiredLevel <= currentLevel;
    internal static bool IsBuiltHideoutRequirementForTest(TrackerRequirement requirement, IReadOnlyDictionary<string, int> levels) =>
        IsBuiltHideoutRequirement(requirement, levels);
    private static string NormalizeMode(string mode) => string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? "pve" : "pvp";
    private static bool Matches(TrackerItem item, string term) => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || item.ShortName.Contains(term, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static TrackerRequirement NormalizeRequirement(TrackerRequirement requirement)
    {
        requirement.Item ??= new TrackerItemStub();
        requirement.Item.Id = string.IsNullOrWhiteSpace(requirement.Item.Id) ? requirement.ItemId : requirement.Item.Id;
        requirement.Item.Name ??= requirement.ItemId;
        requirement.Item.ShortName ??= requirement.Item.Name;
        requirement.SourceType ??= "";
        requirement.SourceId ??= "";
        requirement.SourceName ??= "";
        requirement.SourceDetail ??= "";
        requirement.Trader ??= "";
        return requirement;
    }

    private static TrackerRequirement CloneRequirement(TrackerRequirement source) => new()
    {
        ItemId = source.ItemId,
        Item = CloneStub(source.Item),
        Count = source.Count,
        FoundInRaid = source.FoundInRaid,
        SourceType = source.SourceType,
        SourceId = source.SourceId,
        SourceName = source.SourceName,
        SourceDetail = source.SourceDetail,
        Trader = source.Trader,
        Level = source.Level,
        Station = source.Station,
        Choice = source.Choice,
        PatchSource = source.PatchSource
    };

    private static TrackerItemStub CloneStub(TrackerItemStub source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ShortName = source.ShortName,
        IconLink = source.IconLink,
        GridImageLink = source.GridImageLink,
        Choices = source.Choices?.Select(CloneStub).ToList()
    };

    private static Dictionary<string, Dictionary<string, T>> NormalizeStateValues<T>(Dictionary<string, Dictionary<string, T>>? values) =>
        (values ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

    private static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? ReadInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt32(out var number)) return number;
        return value.TryGetInt64(out var larger) && larger is >= int.MinValue and <= int.MaxValue ? (int)larger : null;
    }

    private static bool ReadBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static bool IsFoundInRaid(JsonElement requirement)
    {
        if (requirement.ValueKind != JsonValueKind.Object || !requirement.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
            return false;
        return attributes.EnumerateArray().Any(attribute =>
            string.Equals(ReadString(attribute, "name"), "foundInRaid", StringComparison.Ordinal) &&
            (ReadBool(attribute, "value") || string.Equals(ReadString(attribute, "value"), "true", StringComparison.OrdinalIgnoreCase)));
    }

    private static string ReadServerHint(string content)
    {
        var singleLine = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrEmpty(singleLine) ? "" : $"：{singleLine[..Math.Min(singleLine.Length, 180)]}";
    }

    private static string Explain(Exception? exception)
    {
        var current = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.FirstOrDefault() : exception;
        return string.IsNullOrWhiteSpace(current?.Message) ? "网络请求未完成" : current.Message;
    }

    private static (string CacheKey, string Url) CreateResource(string gameMode, string resource) =>
        ($"{gameMode}/{resource}", $"{JsonApiBaseUrl}/{gameMode}/{resource}");

    private static IReadOnlyDictionary<string, string> CopyEtags(IReadOnlyDictionary<string, string> etags) =>
        etags.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static string? TryGetEtag(IReadOnlyDictionary<string, string> etags, string key) =>
        etags.TryGetValue(key, out var etag) && !string.IsNullOrWhiteSpace(etag) ? etag : null;

    private static IReadOnlyDictionary<string, string> MergeEtags(
        IReadOnlyDictionary<string, string> existing,
        params JsonApiModeResources[] modes)
    {
        var result = CopyEtags(existing).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var resource in modes.SelectMany(mode => mode.All))
        {
            if (!string.IsNullOrWhiteSpace(resource.ETag)) result[resource.CacheKey] = resource.ETag;
        }
        return result;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/tracker");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed record JsonApiResource(string CacheKey, string Url, byte[]? Payload, string? ETag, bool NotModified)
    {
        public byte[] RequirePayload() => Payload is { Length: > 0 }
            ? Payload
            : throw new InvalidOperationException($"任务 JSON 资源 {CacheKey} 缺少内容。");
    }

    private sealed record JsonApiModeResources(
        string GameMode,
        JsonApiResource Tasks,
        JsonApiResource TasksZh,
        JsonApiResource Hideout,
        JsonApiResource HideoutZh,
        JsonApiResource Items,
        JsonApiResource ItemsZh)
    {
        public IEnumerable<JsonApiResource> All => [Tasks, TasksZh, Hideout, HideoutZh, Items, ItemsZh];
        public bool RequiresRebuild => All.Any(resource => !resource.NotModified);
    }

    private sealed record ModeRefreshResult(
        string Mode,
        IReadOnlyList<TrackerRequirement> Requirements,
        JsonApiModeResources? Resources);

    private sealed class TrackerItemBuilder(string id, string name, string shortName, string iconLink, int have, int? fleaPrice, int? avg24hPrice, bool choice, IReadOnlyList<string> choices)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string ShortName { get; } = shortName;
        public string IconLink { get; } = iconLink;
        public int Have { get; } = have;
        public int? FleaPrice { get; } = fleaPrice;
        public int? Avg24hPrice { get; } = avg24hPrice;
        public bool Choice { get; } = choice;
        public IReadOnlyList<string> Choices { get; } = choices;
        public int Required { get; set; }
        public int CompletedRequired { get; set; }
        public int CompletedTaskRequired { get; set; }
        public int CompletedHideoutRequired { get; set; }
        public int FoundInRaidRequired { get; set; }
        public int TaskRequired { get; set; }
        public int HideoutRequired { get; set; }
        public List<TrackerRequirementSource> Sources { get; } = [];
        public TrackerItem ToItem() => new(Id, Name, ShortName, IconLink, Required, CompletedRequired, CompletedTaskRequired, CompletedHideoutRequired, FoundInRaidRequired, TaskRequired, HideoutRequired, Have, FleaPrice, Avg24hPrice, Sources, Choice, Choices);
    }

    private sealed class TrackerCacheFile
    {
        public int SchemaVersion { get; set; }
        [JsonPropertyName("updatedAt")] public long UpdatedAtUnix { get; set; }
        public string? Source { get; set; }
        public string? PatchSource { get; set; }
        public Dictionary<string, string>? JsonApiEtags { get; set; }
        public Dictionary<string, List<TrackerRequirement>> Requirements { get; set; } = [];
        [JsonIgnore] public DateTimeOffset UpdatedAt =>
            UpdatedAtUnix is > 0 and <= 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds(UpdatedAtUnix)
                : DateTimeOffset.MinValue;
    }

    private sealed class TrackerStateFile
    {
        public int SchemaVersion { get; set; } = 3;
        public Dictionary<string, Dictionary<string, int>> Items { get; set; } = [];
        public Dictionary<string, Dictionary<string, bool>> CompletedSources { get; set; } = [];
        public Dictionary<string, Dictionary<string, int>> HideoutLevels { get; set; } = [];
        [JsonPropertyName("updatedAt")] public long? UpdatedAt { get; set; }
    }

    internal sealed class TrackerRequirement
    {
        public string ItemId { get; set; } = "";
        public TrackerItemStub Item { get; set; } = new();
        public int Count { get; set; }
        public bool FoundInRaid { get; set; }
        public string SourceType { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string SourceDetail { get; set; } = "";
        public string Trader { get; set; } = "";
        public int? Level { get; set; }
        public string? Station { get; set; }
        public bool Choice { get; set; }
        public string? PatchSource { get; set; }
    }

    internal sealed class TrackerItemStub
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ShortName { get; set; } = "";
        public string? IconLink { get; set; }
        public string? GridImageLink { get; set; }
        public List<TrackerItemStub>? Choices { get; set; }
    }
}
