using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Utilities.HideoutProfit;

/// <summary>
/// Loads hideout crafts from the current tarkov.dev flat JSON snapshots and
/// combines them with the application's existing per-mode market catalog.
/// Tool requirements stay visible but are deliberately excluded from cost.
/// </summary>
public static class HideoutProfitService
{
    private const string JsonApiBaseUrl = "https://json.tarkov.dev";
    private const int CacheSchemaVersion = 2;
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly object CacheGate = new();
    private static HideoutProfitCacheFile? _memoryCache;
    private static DateTime _memoryCacheStamp = DateTime.MinValue;
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocatorDesktop",
        "hideout-profit-cache.json");

    public static void Verify()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""
            {"data":[{"id":"craft","station":"station","level":2,"duration":1200,
            "requiredItems":[{"item":"material","count":3,"attributes":{}},{"item":"tool","count":1,"attributes":{"tool":true}}],
            "productItem":{"item":"product","count":2,"attributes":{}}}]}
            """);
        var crafts = ParseCrafts(payload);
        if (crafts is not [{ Level: 2, DurationSeconds: 1200 }] ||
            !crafts[0].Inputs.Any(item => item.IsTool) ||
            CalculateFleaFee(1000, 1000, 2) != 200)
            throw new InvalidDataException("藏身处利润计算验证失败。");
    }

    public static HideoutProfitLoadResult Load(
        string mode,
        IReadOnlyDictionary<string, FeatureMarketItem> market,
        string stationId = "",
        string query = "")
    {
        mode = NormalizeMode(mode);
        var cache = ReadCache();
        if (cache is null || !cache.Crafts.TryGetValue(mode, out var crafts))
            return new HideoutProfitLoadResult([], [], null, false, "正在加载藏身处配方。", 0, 0);

        var rows = crafts.Select(craft => BuildRow(craft, cache.StationNames, market)).ToArray();
        var stations = crafts
            .Select(craft => new HideoutProfitStationOption(
                craft.StationId,
                cache.StationNames.TryGetValue(craft.StationId, out var name) ? name : "未知设施"))
            .DistinctBy(station => station.Id)
            .OrderBy(station => station.Name, StringComparer.CurrentCulture)
            .ToArray();
        var term = query.Trim();
        var visible = rows
            .Where(row => string.IsNullOrWhiteSpace(stationId) || string.Equals(row.StationId, stationId, StringComparison.Ordinal))
            .Where(row => string.IsNullOrWhiteSpace(term) || Matches(row, term))
            .OrderByDescending(row => row.ProfitPerHour.HasValue)
            .ThenByDescending(row => row.ProfitPerHour)
            .ThenBy(row => row.StationName, StringComparer.CurrentCulture)
            .ThenBy(row => row.StationLevel)
            .ToArray();
        return new HideoutProfitLoadResult(
            visible,
            stations,
            GetModeUpdatedAt(cache, mode),
            IsStale(cache, mode),
            null,
            crafts.Length,
            rows.Count(row => row.Profit.HasValue));
    }

    public static async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        var cached = ReadCache();
        if (cached is not null && !IsStale(cached)) return;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HideoutProfitRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resources = new[]
            {
                "regular/crafts",
                "pve/crafts",
                "pvp-season/crafts",
                "regular/hideout",
                "regular/hideout_zh"
            };
            var downloads = await Task.WhenAll(resources.Select(resource => FetchSafeAsync(resource, cancellationToken))).ConfigureAwait(false);
            var payloads = downloads.Where(download => download.Payload is not null)
                .ToDictionary(download => download.Resource, download => download.Payload!, StringComparer.Ordinal);
            var failures = downloads.Where(download => download.Error is not null)
                .Select(download => $"{download.Resource}: {download.Error}")
                .ToList();
            var previous = ReadCache();
            var cache = new HideoutProfitCacheFile
            {
                SchemaVersion = CacheSchemaVersion,
                UpdatedAt = previous?.UpdatedAt ?? DateTimeOffset.MinValue,
                Source = JsonApiBaseUrl,
                StationNames = previous?.StationNames.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal),
                StationNamesUpdatedAt = previous?.StationNamesUpdatedAt ?? previous?.UpdatedAt ?? DateTimeOffset.MinValue,
                Crafts = previous?.Crafts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase),
                CraftUpdatedAt = previous?.CraftUpdatedAt.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase)
            };
            var now = DateTimeOffset.UtcNow;
            var updatedModes = new List<string>();
            foreach (var (resource, mode) in new[]
                     {
                         ("regular/crafts", "pvp"),
                         ("pve/crafts", "pve"),
                         ("pvp-season/crafts", "pvp-season")
                     })
            {
                if (!payloads.TryGetValue(resource, out var payload)) continue;
                try
                {
                    var crafts = ParseCrafts(payload);
                    if (crafts.Length == 0) throw new InvalidOperationException("接口返回了空配方列表");
                    cache.Crafts[mode] = crafts;
                    cache.CraftUpdatedAt[mode] = now;
                    updatedModes.Add(mode);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException)
                {
                    failures.Add($"{resource}: {exception.Message}");
                }
            }

            var stationNamesUpdated = false;
            if (payloads.TryGetValue("regular/hideout", out var hideoutPayload) &&
                payloads.TryGetValue("regular/hideout_zh", out var chinesePayload))
            {
                try
                {
                    var stationNames = ParseStationNames(hideoutPayload, chinesePayload);
                    if (stationNames.Count == 0) throw new InvalidOperationException("接口返回了空设施列表");
                    cache.StationNames = stationNames;
                    cache.StationNamesUpdatedAt = now;
                    stationNamesUpdated = true;
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                {
                    failures.Add($"hideout: {exception.Message}");
                }
            }

            if (cache.Crafts.Count == 0)
                throw new InvalidOperationException(failures.Count > 0
                    ? $"藏身处配方更新失败：{string.Join("；", failures)}"
                    : "藏身处配方接口没有返回可用数据。");

            if (updatedModes.Count > 0 || stationNamesUpdated)
            {
                cache.UpdatedAt = now;
                SaveCache(cache);
            }

            return new HideoutProfitRefreshResult(updatedModes, stationNamesUpdated, failures);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    public static void ReleaseMemory()
    {
        lock (CacheGate)
        {
            _memoryCache = null;
            _memoryCacheStamp = DateTime.MinValue;
        }
    }

    internal static HideoutCraftDefinition[] ParseCrafts(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("藏身处配方 JSON 缺少 data 数组。");
        var result = new List<HideoutCraftDefinition>();
        foreach (var craft in data.EnumerateArray())
        {
            var id = ReadString(craft, "id");
            var station = ReadString(craft, "station");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(station) ||
                !craft.TryGetProperty("productItem", out var product))
                continue;
            var output = ParseIngredient(product);
            if (string.IsNullOrWhiteSpace(output.ItemId) || output.Count <= 0) continue;
            var inputs = craft.TryGetProperty("requiredItems", out var required) && required.ValueKind == JsonValueKind.Array
                ? required.EnumerateArray().Select(ParseIngredient).Where(item => item.Count > 0 && item.ItemId.Length > 0).ToArray()
                : [];
            result.Add(new HideoutCraftDefinition(
                id,
                station,
                Math.Max(1, ReadInt(craft, "level") ?? 1),
                Math.Max(1, ReadInt(craft, "duration") ?? 1),
                inputs,
                output));
        }
        return result.ToArray();
    }

    internal static long CalculateFleaFee(int basePrice, int unitPrice, int count)
    {
        if (basePrice <= 0 || unitPrice <= 0 || count <= 0) return 0;
        var itemWorth = (double)basePrice * count;
        var listingValue = (double)unitPrice * count;
        var itemMultiplier = Math.Log10(itemWorth / listingValue);
        var listingMultiplier = Math.Log10(listingValue / itemWorth);
        if (listingValue >= itemWorth)
            listingMultiplier = Math.Pow(listingMultiplier, 1.08);
        else
            itemMultiplier = Math.Pow(itemMultiplier, 1.08);
        var tax = itemWorth * 0.05 * Math.Pow(4, itemMultiplier) +
                  listingValue * 0.05 * Math.Pow(4, listingMultiplier);
        return double.IsFinite(tax) && tax > 0 ? checked((long)Math.Round(Math.Min(tax, long.MaxValue))) : 0;
    }

    internal static IReadOnlyList<HideoutProfitRow> SortRows(
        IEnumerable<HideoutProfitRow> rows,
        HideoutProfitSortField field,
        bool descending)
    {
        IOrderedEnumerable<HideoutProfitRow> ordered = field switch
        {
            HideoutProfitSortField.Duration => descending
                ? rows.OrderByDescending(row => row.DurationSeconds)
                : rows.OrderBy(row => row.DurationSeconds),
            HideoutProfitSortField.Profit => OrderNullable(rows, row => row.Profit, descending),
            _ => OrderNullable(rows, row => row.ProfitPerHour, descending)
        };
        return ordered
            .ThenBy(row => row.StationName, StringComparer.CurrentCulture)
            .ThenBy(row => row.StationLevel)
            .ThenBy(row => row.Output.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static IOrderedEnumerable<HideoutProfitRow> OrderNullable(
        IEnumerable<HideoutProfitRow> rows,
        Func<HideoutProfitRow, long?> selector,
        bool descending)
    {
        var knownFirst = rows.OrderByDescending(row => selector(row).HasValue);
        return descending
            ? knownFirst.ThenByDescending(row => selector(row))
            : knownFirst.ThenBy(row => selector(row));
    }

    private static HideoutProfitRow BuildRow(
        HideoutCraftDefinition craft,
        IReadOnlyDictionary<string, string> stationNames,
        IReadOnlyDictionary<string, FeatureMarketItem> market)
    {
        var inputs = new List<HideoutProfitIngredient>(craft.Inputs.Length);
        long knownCost = 0;
        var hasUnknownInput = false;
        foreach (var input in craft.Inputs)
        {
            market.TryGetValue(input.ItemId, out var item);
            var acquisition = SelectAcquisition(item);
            if (!input.IsTool)
            {
                if (acquisition.Price is > 0) knownCost += (long)acquisition.Price.Value * input.Count;
                else hasUnknownInput = true;
            }
            inputs.Add(new HideoutProfitIngredient(
                input.ItemId,
                DisplayName(item, input.ItemId),
                input.Count,
                input.IsTool,
                acquisition.Price,
                acquisition.Source,
                item?.IconLink ?? item?.GridImageLink ?? ""));
        }

        market.TryGetValue(craft.Output.ItemId, out var outputItem);
        var sale = SelectSale(outputItem, craft.Output.Count);
        long? inputCost = hasUnknownInput ? null : knownCost;
        long? profit = inputCost.HasValue && sale.Gross.HasValue
            ? sale.Gross.Value - sale.Fee - inputCost.Value
            : null;
        long? hourly = profit.HasValue
            ? checked((long)Math.Round(profit.Value * 3600d / craft.DurationSeconds))
            : null;
        var output = new HideoutProfitIngredient(
            craft.Output.ItemId,
            DisplayName(outputItem, craft.Output.ItemId),
            craft.Output.Count,
            false,
            sale.UnitPrice,
            sale.Source,
            outputItem?.IconLink ?? outputItem?.GridImageLink ?? "");
        return new HideoutProfitRow(
            craft.Id,
            craft.StationId,
            stationNames.TryGetValue(craft.StationId, out var stationName) ? stationName : "未知设施",
            craft.Level,
            inputs,
            output,
            craft.DurationSeconds,
            inputCost,
            sale.Gross,
            sale.Fee,
            profit,
            hourly,
            sale.Source);
    }

    private static (int? Price, string Source) SelectAcquisition(FeatureMarketItem? item)
    {
        if (item is null) return (null, "暂无价格");
        var flea = item.FleaPrice ?? item.Avg24hPrice;
        var trader = item.BestTraderBuy;
        if (trader is not null && trader.Price > 0 && (flea is not > 0 || trader.Price < flea))
            return (trader.Price, $"{trader.VendorName} {trader.MinTraderLevel}级");
        return flea is > 0 ? (flea, "跳蚤市场") : (null, "暂无价格");
    }

    private static SaleSelection SelectSale(FeatureMarketItem? item, int count)
    {
        if (item is null || count <= 0) return new SaleSelection(null, null, 0, "暂无价格");
        var traderGross = item.BestTrader is { Price: > 0 } trader ? (long?)trader.Price * count : null;
        var fleaUnit = item.FleaPrice ?? item.Avg24hPrice;
        long? fleaGross = fleaUnit is > 0 ? (long)fleaUnit.Value * count : null;
        var fleaFee = fleaGross.HasValue && item.BasePrice is > 0
            ? CalculateFleaFee(item.BasePrice.Value, fleaUnit!.Value, count)
            : 0;
        var fleaNet = fleaGross.HasValue && item.BasePrice is > 0 ? fleaGross.Value - fleaFee : (long?)null;
        if (fleaNet.HasValue && (!traderGross.HasValue || fleaNet.Value >= traderGross.Value))
            return new SaleSelection(fleaUnit, fleaGross, fleaFee, "跳蚤市场");
        if (traderGross.HasValue)
            return new SaleSelection(item.BestTrader!.Price, traderGross, 0, $"出售给 {item.BestTrader.VendorName}");
        return new SaleSelection(null, null, 0, "暂无价格");
    }

    private static bool Matches(HideoutProfitRow row, string term) =>
        row.StationName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
        row.Output.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
        row.Inputs.Any(input => input.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase));

    private static string DisplayName(FeatureMarketItem? item, string fallback) =>
        item?.NameZh is { Length: > 0 } name ? name : fallback;

    private static HideoutCraftIngredient ParseIngredient(JsonElement item)
    {
        var attributes = item.TryGetProperty("attributes", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        var isTool = attributes.ValueKind == JsonValueKind.Object &&
                     attributes.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.True;
        return new HideoutCraftIngredient(
            ReadString(item, "item"),
            Math.Max(0, ReadInt(item, "count") ?? 0),
            isTool);
    }

    private static Dictionary<string, string> ParseStationNames(byte[] hideoutPayload, byte[] chinesePayload)
    {
        using var hideout = JsonDocument.Parse(hideoutPayload);
        using var chinese = JsonDocument.Parse(chinesePayload);
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (chinese.RootElement.TryGetProperty("data", out var translated) && translated.ValueKind == JsonValueKind.Object)
            foreach (var property in translated.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } text)
                    translations[property.Name] = text;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!hideout.RootElement.TryGetProperty("data", out var stations) || stations.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in stations.EnumerateObject())
        {
            var station = property.Value;
            var id = ReadString(station, "id");
            if (string.IsNullOrWhiteSpace(id)) id = property.Name;
            var nameKey = ReadString(station, "name");
            var fallback = ReadString(station, "normalizedName");
            result[id] = translations.TryGetValue(nameKey, out var name) ? name : fallback;
        }
        return result;
    }

    private static async Task<byte[]> FetchAsync(string resource, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync($"{JsonApiBaseUrl}/{resource}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("响应超过大小限制。");
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length == 0 || payload.Length > MaximumResponseBytes)
            throw new InvalidDataException("响应为空或超过大小限制。");
        return payload;
    }

    private static async Task<ResourceDownload> FetchSafeAsync(string resource, CancellationToken cancellationToken)
    {
        try
        {
            return new ResourceDownload(resource, await FetchAsync(resource, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            return new ResourceDownload(resource, null, exception.Message);
        }
    }

    private static HideoutProfitCacheFile? ReadCache()
    {
        DateTime stamp;
        try { stamp = File.Exists(CachePath) ? File.GetLastWriteTimeUtc(CachePath) : DateTime.MinValue; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { stamp = DateTime.MinValue; }
        lock (CacheGate)
        {
            if (_memoryCache is not null && stamp == _memoryCacheStamp) return _memoryCache;
            try
            {
                if (stamp == DateTime.MinValue) return null;
                using var stream = File.OpenRead(CachePath);
                var cache = JsonSerializer.Deserialize<HideoutProfitCacheFile>(stream, JsonOptions);
                if (cache is null || cache.SchemaVersion is < 1 or > CacheSchemaVersion) return null;
                cache.Crafts ??= new(StringComparer.OrdinalIgnoreCase);
                cache.CraftUpdatedAt ??= new(StringComparer.OrdinalIgnoreCase);
                cache.StationNames ??= new(StringComparer.Ordinal);
                if (cache.Crafts.Count == 0) return null;
                if (cache.CraftUpdatedAt.Count == 0)
                    foreach (var mode in cache.Crafts.Keys) cache.CraftUpdatedAt[mode] = cache.UpdatedAt;
                if (cache.StationNamesUpdatedAt == DateTimeOffset.MinValue) cache.StationNamesUpdatedAt = cache.UpdatedAt;
                _memoryCache = cache;
                _memoryCacheStamp = stamp;
                return cache;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    private static void SaveCache(HideoutProfitCacheFile cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var temporaryPath = $"{CachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temporaryPath)) JsonSerializer.Serialize(stream, cache, JsonOptions);
            File.Move(temporaryPath, CachePath, overwrite: true);
            lock (CacheGate)
            {
                _memoryCache = cache;
                _memoryCacheStamp = File.GetLastWriteTimeUtc(CachePath);
            }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsStale(HideoutProfitCacheFile cache) =>
        new[] { "pvp", "pve", "pvp-season" }.Any(mode => IsStale(cache, mode));

    private static bool IsStale(HideoutProfitCacheFile cache, string mode)
    {
        var updatedAt = GetModeUpdatedAt(cache, mode);
        return updatedAt is null || DateTimeOffset.UtcNow - updatedAt.Value > CacheLifetime ||
               cache.StationNamesUpdatedAt == DateTimeOffset.MinValue ||
               DateTimeOffset.UtcNow - cache.StationNamesUpdatedAt > CacheLifetime;
    }

    private static DateTimeOffset? GetModeUpdatedAt(HideoutProfitCacheFile cache, string mode) =>
        cache.CraftUpdatedAt.TryGetValue(mode, out var updatedAt) && updatedAt != DateTimeOffset.MinValue
            ? updatedAt
            : cache.UpdatedAt == DateTimeOffset.MinValue ? null : cache.UpdatedAt;

    private static string NormalizeMode(string mode) =>
        string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? "pve" :
        string.Equals(mode, "pvp-season", StringComparison.OrdinalIgnoreCase) ? "pvp-season" : "pvp";

    private static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? ReadInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt32(out var number)) return number;
        return value.TryGetDouble(out var floating) && double.IsFinite(floating) ? checked((int)Math.Round(floating)) : null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/hideout-profit");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    internal sealed record HideoutCraftDefinition(
        string Id,
        string StationId,
        int Level,
        int DurationSeconds,
        HideoutCraftIngredient[] Inputs,
        HideoutCraftIngredient Output);

    internal sealed record HideoutCraftIngredient(string ItemId, int Count, bool IsTool);
    private sealed record SaleSelection(int? UnitPrice, long? Gross, long Fee, string Source);
    private sealed record ResourceDownload(string Resource, byte[]? Payload, string? Error);

    private sealed class HideoutProfitCacheFile
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Source { get; set; } = "";
        public Dictionary<string, string> StationNames { get; set; } = new(StringComparer.Ordinal);
        public DateTimeOffset StationNamesUpdatedAt { get; set; }
        public Dictionary<string, HideoutCraftDefinition[]> Crafts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> CraftUpdatedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record HideoutProfitRefreshResult(
    IReadOnlyList<string> UpdatedModes,
    bool StationNamesUpdated,
    IReadOnlyList<string> Failures)
{
    public bool IsComplete => Failures.Count == 0;
    public bool HasUpdates => UpdatedModes.Count > 0 || StationNamesUpdated;
}
