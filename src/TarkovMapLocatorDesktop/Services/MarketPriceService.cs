using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Loads flea-market prices from json.tarkov.dev. The JSON API exposes a static
/// item snapshot plus separate language maps, so this service joins those
/// resources back into the existing desktop market model.
/// </summary>
public static class MarketPriceService
{
    private const string JsonApiBaseUrl = "https://json.tarkov.dev";
    private const string LegacyApiUrl = "https://api.tarkov.dev/graphql";
    private const string PvpApiGameMode = "regular";
    private const string PvpSeasonApiGameMode = "pvp-season";
    private const string PveApiGameMode = "pve";
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly SemaphoreSlim JsonResourceGate = new(2, 2);
    private static readonly object AutomaticRefreshGate = new();
    private static readonly object CacheGate = new();
    private static Task<MarketCatalog>? _automaticRefreshTask;
    private static MarketCatalog? _memoryCatalog;
    private static IReadOnlyDictionary<string, string> _memoryJsonApiEtags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, DateTime> _memoryCacheStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private static MarketCatalog? _indexedCatalog;
    private static IReadOnlyDictionary<string, MarketItem>? _pvpItemIndex;
    private static IReadOnlyDictionary<string, MarketItem>? _pveItemIndex;
    private static IReadOnlyDictionary<string, MarketItem>? _pvpSeasonItemIndex;

    private static readonly string DesktopCachePath = Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "market-cache.json");

    private static readonly string OriginalCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocator",
        "market-cache.json");

    /// <summary>Returns whether a catalog was built from the current JSON API.</summary>
    public static bool IsJsonApiCatalog(MarketCatalog catalog) =>
        catalog.Source.StartsWith(JsonApiBaseUrl, StringComparison.OrdinalIgnoreCase);

    public static MarketCatalog Load()
    {
        var stamps = GetCacheStamps();
        lock (CacheGate)
        {
            if (_memoryCatalog is not null && HasSameCacheStamps(stamps)) return _memoryCatalog;

            var candidates = new List<(CacheSnapshot Snapshot, DateTime Stamp)>();
            foreach (var candidate in new[] { OriginalCachePath, DesktopCachePath }
                         .Select(path => new { Path = path, Stamp = GetCacheStamp(path) })
                         .Where(candidate => candidate.Stamp != DateTime.MinValue))
            {
                var cached = TryRead(candidate.Path);
                if (cached is not null) candidates.Add((cached, candidate.Stamp));
            }

            // Once a JSON catalog exists, prefer it over an old GraphQL cache even
            // if the old launcher wrote a slightly newer timestamp.
            var selected = candidates
                .Where(candidate => candidate.Snapshot.Catalog.IsAvailable)
                .OrderByDescending(candidate => candidate.Snapshot.Catalog.IsComplete)
                .ThenByDescending(candidate => IsJsonApiCatalog(candidate.Snapshot.Catalog))
                .ThenByDescending(candidate => candidate.Stamp)
                .Select(candidate => candidate.Snapshot)
                .FirstOrDefault();

            _memoryCatalog = selected?.Catalog ?? new MarketCatalog(
                [], [], null, LegacyApiUrl, "尚未找到行情缓存。点击“刷新价格”获取最新数据。");
            _memoryJsonApiEtags = selected?.JsonApiEtags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _memoryCacheStamps = stamps;
            return _memoryCatalog;
        }
    }

    /// <summary>
    /// Reuses the item-id index while a market catalog is unchanged. The tracker calls
    /// this path after every quantity update, so rebuilding a several-thousand-item
    /// dictionary there would otherwise create unnecessary UI-visible GC pressure.
    /// </summary>
    public static IReadOnlyDictionary<string, MarketItem> GetItemIndex(string mode)
    {
        var catalog = Load();
        var isPve = string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase);
        var isSeason = string.Equals(mode, PvpSeasonApiGameMode, StringComparison.OrdinalIgnoreCase);
        lock (CacheGate)
        {
            if (!ReferenceEquals(_indexedCatalog, catalog))
            {
                _indexedCatalog = catalog;
                _pvpItemIndex = null;
                _pveItemIndex = null;
                _pvpSeasonItemIndex = null;
            }

            if (isPve)
                return _pveItemIndex ??= catalog.PveItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
            if (isSeason)
                return _pvpSeasonItemIndex ??= (catalog.PvpSeasonItems ?? []).ToDictionary(item => item.Id, StringComparer.Ordinal);

            return _pvpItemIndex ??= catalog.PvpItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Drops only rebuildable process-local state. The durable market cache is
    /// intentionally preserved so reopening a feature does not require a network
    /// refresh.
    /// </summary>
    public static void ReleaseMemory()
    {
        lock (CacheGate)
        {
            _memoryCatalog = null;
            _memoryJsonApiEtags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _memoryCacheStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _indexedCatalog = null;
            _pvpItemIndex = null;
            _pveItemIndex = null;
            _pvpSeasonItemIndex = null;
        }

        lock (AutomaticRefreshGate)
        {
            if (_automaticRefreshTask?.IsCompleted is not false) _automaticRefreshTask = null;
        }
    }

    /// <summary>
    /// Keeps the 12-hour display-cache lifecycle. A legacy GraphQL cache is never
    /// treated as migration-complete, so the first JSON-API capable run replaces it.
    /// </summary>
    public static Task<MarketCatalog> EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        var cached = Load();
        if (cached.IsComplete && !cached.IsStale && IsJsonApiCatalog(cached) &&
            cached.PvpItems.Any(item => item.BasePrice is > 0) &&
            cached.PveItems.Any(item => item.BasePrice is > 0) &&
            cached.PvpSeasonItems?.Any(item => item.BasePrice is > 0) == true)
            return Task.FromResult(cached);

        lock (AutomaticRefreshGate)
        {
            if (_automaticRefreshTask is { IsCompleted: false }) return _automaticRefreshTask;
            _automaticRefreshTask = RefreshAsync(cancellationToken);
            return _automaticRefreshTask;
        }
    }

    /// <summary>
    /// Refreshes only market data. Item resources use ETags so an unchanged API
    /// snapshot is validated without downloading and reparsing all price rows again.
    /// </summary>
    public static async Task<MarketCatalog> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = Load();
            var etags = GetLoadedEtags();
            var refreshed = await RefreshJsonApiAsync(cached, etags, cancellationToken).ConfigureAwait(false);
            Save(refreshed.Catalog, refreshed.JsonApiEtags);
            return refreshed.Catalog;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<JsonApiRefreshResult> RefreshJsonApiAsync(
        MarketCatalog cached,
        IReadOnlyDictionary<string, string> etags,
        CancellationToken cancellationToken)
    {
        var refreshes = await Task.WhenAll(
            RefreshModeAsync(PvpApiGameMode, cached.PvpItems, etags, cancellationToken),
            RefreshModeAsync(PveApiGameMode, cached.PveItems, etags, cancellationToken),
            RefreshModeAsync(PvpSeasonApiGameMode, cached.PvpSeasonItems ?? [], etags, cancellationToken)).ConfigureAwait(false);
        var errors = refreshes.Where(result => result.Error is not null).Select(result => result.Error!).ToArray();
        var catalog = new MarketCatalog(
            refreshes[0].Items,
            refreshes[1].Items,
            errors.Length == 0 ? DateTimeOffset.Now : cached.UpdatedAt,
            JsonApiBaseUrl,
            errors.Length == 0 ? null : string.Join("；", errors),
            refreshes[2].Items);
        if (!catalog.IsAvailable)
            throw new InvalidOperationException("行情服务没有返回可用物品数据，且没有本地缓存。");

        var successfulResources = refreshes.Select(result => result.Resources).OfType<JsonApiModeResources>().ToArray();
        return new JsonApiRefreshResult(catalog, MergeEtags(etags, successfulResources));
    }

    private static async Task<ModeRefreshResult> RefreshModeAsync(
        string gameMode,
        IReadOnlyList<MarketItem> cachedItems,
        IReadOnlyDictionary<string, string> etags,
        CancellationToken cancellationToken)
    {
        try
        {
            var resources = await FetchModeResourcesAsync(gameMode, etags, cancellationToken).ConfigureAwait(false);
            if (!resources.RequiresRebuild(cachedItems)) return new ModeRefreshResult(cachedItems, resources, null);

            resources = await EnsureModePayloadsAsync(resources, cancellationToken).ConfigureAwait(false);
            var traderNames = await FetchTraderNamesAsync(cancellationToken).ConfigureAwait(false);
            var items = ParseJsonApiMarketItems(resources, traderNames);
            if (items.Count == 0) throw new InvalidOperationException("接口返回空数据");
            return new ModeRefreshResult(items, resources, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var displayName = gameMode switch
            {
                PveApiGameMode => "PVE",
                PvpSeasonApiGameMode => "赛季服",
                _ => "PVP"
            };
            RuntimeLogService.Warning("市场", $"{displayName} 行情刷新失败，保留该模式缓存", exception.Message);
            return new ModeRefreshResult(cachedItems, null, $"{displayName} 刷新失败，已保留缓存");
        }
    }

    private static async Task<JsonApiModeResources> FetchModeResourcesAsync(
        string gameMode,
        IReadOnlyDictionary<string, string> etags,
        CancellationToken cancellationToken)
    {
        var items = CreateResource(gameMode, "items");
        var chinese = CreateResource(gameMode, "items_zh");
        var english = CreateResource(gameMode, "items_en");
        var tasks = new[]
        {
            FetchJsonResourceAsync(items.CacheKey, items.Url, TryGetEtag(etags, items.CacheKey), cancellationToken),
            FetchJsonResourceAsync(chinese.CacheKey, chinese.Url, TryGetEtag(etags, chinese.CacheKey), cancellationToken),
            FetchJsonResourceAsync(english.CacheKey, english.Url, TryGetEtag(etags, english.CacheKey), cancellationToken)
        };
        var resources = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new JsonApiModeResources(gameMode, resources[0], resources[1], resources[2]);
    }

    private static async Task<JsonApiModeResources> EnsureModePayloadsAsync(
        JsonApiModeResources resources,
        CancellationToken cancellationToken)
    {
        var itemsTask = EnsurePayloadAsync(resources.Items, cancellationToken);
        var chineseTask = EnsurePayloadAsync(resources.Chinese, cancellationToken);
        var englishTask = EnsurePayloadAsync(resources.English, cancellationToken);
        await Task.WhenAll(itemsTask, chineseTask, englishTask).ConfigureAwait(false);
        return resources with
        {
            Items = itemsTask.Result,
            Chinese = chineseTask.Result,
            English = englishTask.Result
        };
    }

    private static Task<JsonApiResource> EnsurePayloadAsync(JsonApiResource resource, CancellationToken cancellationToken) =>
        resource.Payload is { Length: > 0 }
            ? Task.FromResult(resource)
            : FetchJsonResourceAsync(resource.CacheKey, resource.Url, null, cancellationToken);

    private static async Task<IReadOnlyDictionary<string, string>> FetchTraderNamesAsync(CancellationToken cancellationToken)
    {
        // Trader metadata is small. It is intentionally fetched only when an item
        // snapshot changed, so cached item prices do not need to persist raw JSON.
        var traders = CreateResource("regular", "traders");
        var chinese = CreateResource("regular", "traders_zh");
        var tradersTask = FetchJsonResourceAsync(traders.CacheKey, traders.Url, null, cancellationToken);
        var chineseTask = FetchJsonResourceAsync(chinese.CacheKey, chinese.Url, null, cancellationToken);
        await Task.WhenAll(tradersTask, chineseTask).ConfigureAwait(false);
        return ParseJsonApiTraderNames(tradersTask.Result.RequirePayload(), chineseTask.Result.RequirePayload());
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
                $"行情 JSON 资源 {cacheKey}",
                cancellationToken,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new JsonApiResource(cacheKey, url, null, etag, true);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{ReadServerHint(content)}",
                    null,
                    response.StatusCode);
            }

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (payload.Length == 0) throw new InvalidOperationException($"行情 JSON 资源 {cacheKey} 返回为空。");
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

    internal static IReadOnlyList<MarketItem> ParseJsonApiMarketItems(
        byte[] itemsPayload,
        byte[] chinesePayload,
        byte[] englishPayload,
        IReadOnlyDictionary<string, string> traderNames) =>
        ParseJsonApiMarketItems(
            new JsonApiModeResources(
                "test",
                new JsonApiResource("items", "", itemsPayload, null, false),
                new JsonApiResource("items_zh", "", chinesePayload, null, false),
                new JsonApiResource("items_en", "", englishPayload, null, false)),
            traderNames);

    private static IReadOnlyList<MarketItem> ParseJsonApiMarketItems(
        JsonApiModeResources resources,
        IReadOnlyDictionary<string, string> traderNames)
    {
        using var itemsDocument = JsonDocument.Parse(resources.Items.RequirePayload());
        using var chineseDocument = JsonDocument.Parse(resources.Chinese.RequirePayload());
        using var englishDocument = JsonDocument.Parse(resources.English.RequirePayload());
        var translationsZh = ReadTranslationMap(chineseDocument.RootElement);
        var translationsEn = ReadTranslationMap(englishDocument.RootElement);
        if (!itemsDocument.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("行情 JSON 没有返回 data.items 对象。");

        var result = new List<MarketItem>();
        foreach (var itemProperty in items.EnumerateObject())
        {
            var item = itemProperty.Value;
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) id = itemProperty.Name;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var rawName = ReadString(item, "name");
            var rawShortName = ReadString(item, "shortName");
            var fallbackName = ReadString(item, "normalizedName");
            var nameZh = Translate(rawName, translationsZh, fallbackName);
            var shortNameZh = Translate(rawShortName, translationsZh, nameZh);
            var nameEn = Translate(rawName, translationsEn, fallbackName);
            var shortNameEn = Translate(rawShortName, translationsEn, nameEn);
            var fleaPrice = ReadInt(item, "lastLowPrice") ?? ReadInt(item, "avg24hPrice");
            var offers = ReadJsonApiOffers(item, traderNames).ToList();
            var bestTraderBuy = ReadJsonApiBuyOffers(item, traderNames)
                .OrderBy(offer => offer.Price)
                .FirstOrDefault();
            if (fleaPrice is > 0)
                offers.Add(new MarketSellOffer(fleaPrice.Value, "fleaMarket", "跳蚤市场"));

            result.Add(BuildItem(
                id,
                nameZh,
                shortNameZh,
                nameEn,
                shortNameEn,
                ReadInt(item, "avg24hPrice"),
                ReadInt(item, "low24hPrice"),
                ReadInt(item, "high24hPrice"),
                ReadInt(item, "lastLowPrice"),
                offers,
                cachedFleaPrice: fleaPrice,
                iconLink: ReadString(item, "iconLink"),
                gridImageLink: ReadString(item, "gridImageLink"),
                types: ReadStrings(item, "types"),
                width: ReadInt(item, "width"),
                height: ReadInt(item, "height"),
                basePrice: ReadInt(item, "basePrice"),
                bestTraderBuy: bestTraderBuy));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseJsonApiTraderNames(byte[] tradersPayload, byte[] chinesePayload)
    {
        using var tradersDocument = JsonDocument.Parse(tradersPayload);
        using var chineseDocument = JsonDocument.Parse(chinesePayload);
        var translations = ReadTranslationMap(chineseDocument.RootElement);
        if (!tradersDocument.RootElement.TryGetProperty("data", out var traders) || traders.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("行情 JSON 没有返回 data.traders 对象。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var traderProperty in traders.EnumerateObject())
        {
            var trader = traderProperty.Value;
            var id = ReadString(trader, "id");
            if (string.IsNullOrWhiteSpace(id)) id = traderProperty.Name;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var rawName = ReadString(trader, "name");
            var localizedName = Translate(rawName, translations, ReadString(trader, "normalizedName"));
            result[id] = string.IsNullOrWhiteSpace(localizedName) ? id : localizedName;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadTranslationMap(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("行情 JSON 语言映射缺少 data 对象。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value)) result[property.Name] = value;
        }

        return result;
    }

    private static IEnumerable<MarketSellOffer> ReadJsonApiOffers(
        JsonElement item,
        IReadOnlyDictionary<string, string> traderNames)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("sellToTrader", out var sellToTrader) ||
            sellToTrader.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var offer in sellToTrader.EnumerateArray())
        {
            // The raw price is expressed in the offer currency. The market UI is
            // RUB-only, so never fall back to a USD/EUR amount here.
            var price = ReadInt(offer, "priceRUB");
            if (price is not > 0) continue;
            var traderId = ReadString(offer, "trader");
            if (string.IsNullOrWhiteSpace(traderId) && offer.TryGetProperty("trader", out var trader) && trader.ValueKind == JsonValueKind.Object)
                traderId = ReadString(trader, "id");
            var vendorName = traderNames.TryGetValue(traderId, out var name) ? name : traderId;
            yield return new MarketSellOffer(price.Value, "trader", string.IsNullOrWhiteSpace(vendorName) ? "商人" : vendorName);
        }
    }

    private static IEnumerable<MarketBuyOffer> ReadJsonApiBuyOffers(
        JsonElement item,
        IReadOnlyDictionary<string, string> traderNames)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("buyFromTrader", out var buyFromTrader) ||
            buyFromTrader.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var offer in buyFromTrader.EnumerateArray())
        {
            var price = ReadInt(offer, "priceRUB");
            if (price is not > 0) continue;
            var traderId = ReadString(offer, "trader");
            if (string.IsNullOrWhiteSpace(traderId) && offer.TryGetProperty("trader", out var trader) && trader.ValueKind == JsonValueKind.Object)
                traderId = ReadString(trader, "id");
            var vendorName = traderNames.TryGetValue(traderId, out var name) ? name : traderId;
            yield return new MarketBuyOffer(
                price.Value,
                string.IsNullOrWhiteSpace(vendorName) ? "商人" : vendorName,
                Math.Max(1, ReadInt(offer, "minTraderLevel") ?? 1));
        }
    }

    private static string Translate(string rawValue, IReadOnlyDictionary<string, string> translations, string fallback) =>
        !string.IsNullOrWhiteSpace(rawValue) && translations.TryGetValue(rawValue, out var translated) && !string.IsNullOrWhiteSpace(translated)
            ? translated
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : rawValue;

    private static CacheSnapshot? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var cached = JsonSerializer.Deserialize<MarketCacheFile>(stream, JsonOptions);
            if (cached is null) return null;
            DateTimeOffset? updatedAt = cached.MarketTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)cached.MarketTimestamp)
                : null;
            var catalog = new MarketCatalog(
                (cached.MarketItemsPvp ?? []).Select(ToMarketItem).ToArray(),
                (cached.MarketItemsPve ?? []).Select(ToMarketItem).ToArray(),
                updatedAt,
                cached.Source ?? LegacyApiUrl,
                null,
                (cached.MarketItemsPvpSeason ?? []).Select(ToMarketItem).ToArray());
            var etags = (cached.JsonApiEtags ?? new Dictionary<string, string>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return new CacheSnapshot(catalog, etags);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            RuntimeLogService.Warning("市场", "读取行情缓存失败，继续尝试其他缓存", $"文件: {path}\n{exception}");
            return null;
        }
    }

    private static void Save(MarketCatalog catalog, IReadOnlyDictionary<string, string> jsonApiEtags)
    {
        var copiedEtags = CopyEtags(jsonApiEtags);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopCachePath)!);
            var file = new MarketCacheFile
            {
                MarketTimestamp = catalog.UpdatedAt?.ToUnixTimeSeconds() ?? DateTimeOffset.Now.ToUnixTimeSeconds(),
                Source = catalog.Source,
                JsonApiEtags = copiedEtags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                MarketItemsPvp = catalog.PvpItems.Select(FromMarketItem).ToArray(),
                MarketItemsPve = catalog.PveItems.Select(FromMarketItem).ToArray(),
                MarketItemsPvpSeason = (catalog.PvpSeasonItems ?? []).Select(FromMarketItem).ToArray()
            };
            var temporaryPath = $"{DesktopCachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = File.Create(temporaryPath)) JsonSerializer.Serialize(stream, file, JsonOptions);
                File.Move(temporaryPath, DesktopCachePath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogService.Warning("市场", "清理行情缓存临时文件失败", exception.Message);
                }
            }

            SetMemory(catalog, copiedEtags);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A successful refresh remains usable in this process even if the next
            // launch cannot reuse it from disk.
            SetMemory(catalog, copiedEtags);
            RuntimeLogService.Warning("市场", "最新行情已载入内存，但写入本地缓存失败", exception.ToString());
        }
    }

    private static void SetMemory(MarketCatalog catalog, IReadOnlyDictionary<string, string> etags)
    {
        lock (CacheGate)
        {
            _memoryCatalog = catalog;
            _memoryJsonApiEtags = CopyEtags(etags);
            _memoryCacheStamps = GetCacheStamps();
        }
    }

    private static IReadOnlyDictionary<string, string> GetLoadedEtags()
    {
        lock (CacheGate) return CopyEtags(_memoryJsonApiEtags);
    }

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

    private static IReadOnlyDictionary<string, string> CopyEtags(IReadOnlyDictionary<string, string> etags) =>
        etags.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static string? TryGetEtag(IReadOnlyDictionary<string, string> etags, string cacheKey) =>
        etags.TryGetValue(cacheKey, out var etag) && !string.IsNullOrWhiteSpace(etag) ? etag : null;

    private static (string CacheKey, string Url) CreateResource(string gameMode, string resource) =>
        ($"{gameMode}/{resource}", $"{JsonApiBaseUrl}/{gameMode}/{resource}");

    private static MarketItem ToMarketItem(MarketCacheItem item) => BuildItem(
        item.Id ?? "", item.NameZh ?? "", item.ShortNameZh ?? "", item.Name ?? "", item.ShortName ?? "",
        item.Avg24hPrice, item.Low24hPrice, item.High24hPrice, item.LastLowPrice,
        (item.SellFor ?? []).Where(offer => offer.Price > 0).Select(offer => new MarketSellOffer(offer.Price, offer.Source ?? "", offer.VendorName ?? "")).ToArray(),
        item.FleaPrice,
        item.BestTrader is null ? null : new MarketSellOffer(item.BestTrader.Price, item.BestTrader.Source ?? "", item.BestTrader.VendorName ?? ""),
        item.IconLink ?? "", item.GridImageLink ?? "", item.Types ?? [], item.Width, item.Height,
        item.BasePrice,
        item.BestTraderBuy is null ? null : new MarketBuyOffer(item.BestTraderBuy.Price, item.BestTraderBuy.VendorName ?? "", item.BestTraderBuy.MinTraderLevel));

    private static MarketItem BuildItem(
        string id, string nameZh, string shortNameZh, string name, string shortName,
        int? avg24h, int? low24h, int? high24h, int? lastLow, IReadOnlyList<MarketSellOffer> offers,
        int? cachedFleaPrice = null, MarketSellOffer? cachedBestTrader = null,
        string iconLink = "", string gridImageLink = "", IReadOnlyList<string>? types = null, int? width = null, int? height = null,
        int? basePrice = null, MarketBuyOffer? bestTraderBuy = null)
    {
        var flea = cachedFleaPrice ?? offers.FirstOrDefault(offer => string.Equals(offer.Source, "fleaMarket", StringComparison.OrdinalIgnoreCase))?.Price;
        var traders = offers.Where(offer => !string.Equals(offer.Source, "fleaMarket", StringComparison.OrdinalIgnoreCase)).ToArray();
        var bestTrader = cachedBestTrader ?? traders.OrderByDescending(offer => offer.Price).FirstOrDefault();
        return new MarketItem(id, nameZh, shortNameZh, string.IsNullOrWhiteSpace(name) ? nameZh : name, string.IsNullOrWhiteSpace(shortName) ? shortNameZh : shortName,
            avg24h, low24h, high24h, lastLow, flea, bestTrader, offers, iconLink, gridImageLink, types ?? [], width, height, basePrice, bestTraderBuy);
    }

    private static MarketCacheItem FromMarketItem(MarketItem item) => new()
    {
        Id = item.Id, NameZh = item.NameZh, ShortNameZh = item.ShortNameZh, Name = item.Name, ShortName = item.ShortName,
        Avg24hPrice = item.Avg24hPrice, Low24hPrice = item.Low24hPrice, High24hPrice = item.High24hPrice, LastLowPrice = item.LastLowPrice,
        FleaPrice = item.FleaPrice, IconLink = item.IconLink, GridImageLink = item.GridImageLink, Types = item.Types?.ToArray(), Width = item.Width, Height = item.Height,
        BasePrice = item.BasePrice,
        BestTraderBuy = item.BestTraderBuy is null ? null : new MarketCacheBuyOffer { Price = item.BestTraderBuy.Price, VendorName = item.BestTraderBuy.VendorName, MinTraderLevel = item.BestTraderBuy.MinTraderLevel },
        BestTrader = item.BestTrader is null ? null : new MarketCacheOffer { Price = item.BestTrader.Price, Source = item.BestTrader.Source, VendorName = item.BestTrader.VendorName },
        SellFor = item.SellFor.Select(offer => new MarketCacheOffer { Price = offer.Price, Source = offer.Source, VendorName = offer.VendorName }).ToArray()
    };

    private static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "").Where(value => value.Length > 0).ToArray()
            : [];

    private static int? ReadInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt32(out var number)) return number;
        return value.TryGetInt64(out var larger) && larger is >= int.MinValue and <= int.MaxValue ? (int)larger : null;
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

    private static IReadOnlyDictionary<string, DateTime> GetCacheStamps() =>
        new[] { OriginalCachePath, DesktopCachePath }
            .ToDictionary(path => path, GetCacheStamp, StringComparer.OrdinalIgnoreCase);

    private static DateTime GetCacheStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static bool HasSameCacheStamps(IReadOnlyDictionary<string, DateTime> stamps) =>
        _memoryCacheStamps.Count == stamps.Count && stamps.All(pair => _memoryCacheStamps.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/market");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed record CacheSnapshot(MarketCatalog Catalog, IReadOnlyDictionary<string, string> JsonApiEtags);

    private sealed record JsonApiRefreshResult(MarketCatalog Catalog, IReadOnlyDictionary<string, string> JsonApiEtags);

    private sealed record ModeRefreshResult(IReadOnlyList<MarketItem> Items, JsonApiModeResources? Resources, string? Error);

    private sealed record JsonApiResource(string CacheKey, string Url, byte[]? Payload, string? ETag, bool NotModified)
    {
        public byte[] RequirePayload() => Payload is { Length: > 0 }
            ? Payload
            : throw new InvalidOperationException($"行情 JSON 资源 {CacheKey} 缺少内容。");
    }

    private sealed record JsonApiModeResources(
        string GameMode,
        JsonApiResource Items,
        JsonApiResource Chinese,
        JsonApiResource English)
    {
        public IEnumerable<JsonApiResource> All => [Items, Chinese, English];
        public bool RequiresRebuild(IReadOnlyList<MarketItem> cachedItems) =>
            cachedItems.Count == 0 || cachedItems.All(item => item.BasePrice is not > 0) ||
            !Items.NotModified || !Chinese.NotModified || !English.NotModified;
    }

    private sealed class MarketCacheFile
    {
        public double MarketTimestamp { get; set; }
        public string? Source { get; set; }
        public Dictionary<string, string>? JsonApiEtags { get; set; }
        public MarketCacheItem[]? MarketItemsPvp { get; set; }
        public MarketCacheItem[]? MarketItemsPve { get; set; }
        public MarketCacheItem[]? MarketItemsPvpSeason { get; set; }
    }

    private sealed class MarketCacheItem
    {
        public string? Id { get; set; }
        public string? NameZh { get; set; }
        public string? ShortNameZh { get; set; }
        public string? Name { get; set; }
        public string? ShortName { get; set; }
        public int? Avg24hPrice { get; set; }
        public int? Low24hPrice { get; set; }
        public int? High24hPrice { get; set; }
        public int? LastLowPrice { get; set; }
        public int? FleaPrice { get; set; }
        public string? IconLink { get; set; }
        public string? GridImageLink { get; set; }
        public string[]? Types { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? BasePrice { get; set; }
        public MarketCacheBuyOffer? BestTraderBuy { get; set; }
        public MarketCacheOffer? BestTrader { get; set; }
        public MarketCacheOffer[]? SellFor { get; set; }
    }

    private sealed class MarketCacheOffer
    {
        public int Price { get; set; }
        public string? Source { get; set; }
        public string? VendorName { get; set; }
    }

    private sealed class MarketCacheBuyOffer
    {
        public int Price { get; set; }
        public string? VendorName { get; set; }
        public int MinTraderLevel { get; set; }
    }
}
