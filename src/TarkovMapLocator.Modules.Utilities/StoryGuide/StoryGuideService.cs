using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TarkovMapLocator.Modules.Utilities;

namespace TarkovMapLocator.Modules.Utilities.StoryGuide;

internal static class StoryGuideService
{
    private const int MaximumResponseBytes = 3 * 1024 * 1024;
    private const int MaximumCachedChapters = 12;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<StoryGuideLoadResult>> Pending = new(StringComparer.Ordinal);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, StoryGuideDetail> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LinkedListNode<string>> CacheNodes = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> CacheOrder = [];
    private static readonly Regex EftStepMarkerPattern = new(
        "<h4\\b[^>]*>(?<title>[\\s\\S]*?)</h4>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex ImagePattern = new(
        "<img\\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex TagPattern = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex SvgPattern = new(
        "<svg\\b[\\s\\S]*?</svg>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex BreakPattern = new(
        "<br\\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex WhitespacePattern = new(
        "\\s+",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly IReadOnlyList<StoryGuideChapter> Chapters =
    [
        new("tour", "塔科夫之旅", "Tour", "序章 / 生存路线", "从中心区脱身，逐步解锁地图并调查离开塔科夫的路线。"),
        new("batya", "巴蒂亚", "Batya", "人物线 / 关系网", "追查进入塔科夫的 BEAR 特种部队及其秘密任务。"),
        new("falling-skies", "陨落星辰", "Falling Skies", "异常事件 / 调查", "围绕森林坠机现场、飞行记录和后续线索展开调查。"),
        new("accidental-witness", "意外证人", "Accidental Witness", "证据 / 情报", "从宿舍附近的恐吓信息入手，追踪债务与幕后人物。"),
        new("they-are-already-here", "他们已经来了", "They Are Already Here", "威胁 / 未知势力", "调查邪教圆圈、神秘符号与未知势力留下的痕迹。"),
        new("the-unheard", "无名者", "The Unheard", "隐藏线 / 深层线索", "追查 TerraGroup、“无声者”与净化计划之间的联系。"),
        new("blue-fire", "神秘蓝焰", "Blue Fire", "异常现象 / 特殊物资", "调查屋顶爆炸、蓝色闪光和大范围电子设备失灵事件。"),
        new("the-labyrinth", "迷宫", "The Labyrinth", "高风险区域 / 终局路线", "深入海岸线地下设施，寻找 TerraGroup 隐藏计划的证据。"),
        new("the-ticket", "门票", "The Ticket", "通行资格 / 撤离线索", "围绕通行资格、撤离条件和后续去向推进剧情。"),
        new("boreas", "北风", "Boreas", "主线剧情 / 破冰船线索", "追踪求救信号、航运文件和前往破冰船的交通安排。")
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<StoryGuideVariant>> Variants =
        new Dictionary<string, IReadOnlyList<StoryGuideVariant>>(StringComparer.Ordinal)
        {
            ["tour"] = [CreateVariant("default", "塔科夫之旅", "4906")],
            ["batya"] = [CreateVariant("default", "巴蒂亚", "4910")],
            ["falling-skies"] = [CreateVariant("default", "陨落星辰", "4909")],
            ["accidental-witness"] = [CreateVariant("default", "意外证人", "4911")],
            ["they-are-already-here"] = [CreateVariant("default", "他们已经来了", "4912")],
            ["the-unheard"] = [CreateVariant("default", "无名者", "4917")],
            ["blue-fire"] = [CreateVariant("default", "神秘蓝焰", "4913")],
            ["the-labyrinth"] = [CreateVariant("default", "迷宫", "4916")],
            ["the-ticket"] =
            [
                CreateVariant("humanity-submit", "为了全人类 · 上交箱子", "5355"),
                CreateVariant("humanity-keep", "为了全人类 · 不交箱子", "4914"),
                CreateVariant("lightkeeper-submit", "灯塔 · 上交箱子", "5356"),
                CreateVariant("lightkeeper-keep", "灯塔 · 不交箱子", "4915"),
                CreateVariant("survivor-submit", "幸存者 · 上交箱子", "5357"),
                CreateVariant("survivor-keep", "幸存者 · 不交箱子", "5211"),
                CreateVariant("darkness-submit", "堕入黑暗 · 上交箱子", "5358"),
                CreateVariant("darkness-keep", "堕入黑暗 · 不交箱子", "5212")
            ],
            ["boreas"] = [CreateVariant("default", "北风", "5557")]
        };

    public static IReadOnlyList<StoryGuideChapter> LoadCatalog() => Chapters;

    public static void Verify()
    {
        if (Chapters.Count != 10 || Chapters.Select(chapter => chapter.Id).Distinct(StringComparer.Ordinal).Count() != Chapters.Count)
            throw new InvalidDataException("剧情攻略章节目录不完整。");
        var ticket = Chapters.Single(chapter => chapter.Id == "the-ticket");
        if (LoadVariants(ticket).Count != 8)
            throw new InvalidDataException("门票剧情结局选项不完整。");
        const string fixture = "<article id=\"newsContent\"><span class=\"rwgl\">剧情攻略</span><h4>测试步骤</h4><p>测试内容</p></article>";
        if (ParseEftarkov(ticket, fixture).Steps.Count != 1)
            throw new InvalidDataException("剧情攻略解析验证失败。");
    }

    public static IReadOnlyList<StoryGuideVariant> LoadVariants(StoryGuideChapter chapter) =>
        Variants.TryGetValue(chapter.Id, out var variants) ? variants : [];

    public static Task<StoryGuideLoadResult> GetAsync(StoryGuideChapter chapter, bool forceRefresh = false) =>
        GetAsync(chapter, LoadVariants(chapter).First(), forceRefresh);

    public static Task<StoryGuideLoadResult> GetAsync(StoryGuideChapter chapter, StoryGuideVariant variant, bool forceRefresh = false)
        => GetAsync(chapter, variant, "pve", forceRefresh);

    public static async Task<StoryGuideLoadResult> GetAsync(
        StoryGuideChapter chapter,
        StoryGuideVariant variant,
        string gameMode,
        bool forceRefresh = false)
    {
        var result = await GetRawAsync(chapter, variant, forceRefresh).ConfigureAwait(false);
        return result.Detail is { } detail
            ? result with { Detail = FilterForGameMode(detail, gameMode) }
            : result;
    }

    private static Task<StoryGuideLoadResult> GetRawAsync(
        StoryGuideChapter chapter,
        StoryGuideVariant variant,
        bool forceRefresh)
    {
        var cacheKey = $"{chapter.Id}:{variant.Id}";
        if (!forceRefresh && TryGetCached(cacheKey, out var cached))
            return Task.FromResult(new StoryGuideLoadResult(cached, null));

        TryGetCached(cacheKey, out var fallback);
        return Pending.GetOrAdd(cacheKey, _ => LoadAndCacheAsync(chapter, variant, cacheKey, fallback));
    }

    internal static StoryGuideDetail ParseEftarkovForTest(StoryGuideChapter chapter, string html) => ParseEftarkov(chapter, html);

    internal static StoryGuideDetail FilterForGameModeForTest(StoryGuideDetail detail, string gameMode) =>
        FilterForGameMode(detail, gameMode);

    private static async Task<StoryGuideLoadResult> LoadAndCacheAsync(
        StoryGuideChapter chapter,
        StoryGuideVariant variant,
        string cacheKey,
        StoryGuideDetail? fallback)
    {
        try
        {
            var html = await DownloadAsync(variant.SourceUrl).ConfigureAwait(false);
            var detail = ParseEftarkov(chapter, html);
            CacheDetail(cacheKey, detail);
            return new StoryGuideLoadResult(detail, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or RegexMatchTimeoutException)
        {
            return fallback is not null
                ? new StoryGuideLoadResult(fallback, "剧情攻略刷新失败，已保留原内容。", true)
                : new StoryGuideLoadResult(null, "剧情攻略加载失败，请检查网络后重试。");
        }
        finally
        {
            Pending.TryRemove(cacheKey, out _);
        }
    }

    private static async Task<string> DownloadAsync(string url)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !string.Equals(finalUri.Host, "www.eftarkov.com", StringComparison.OrdinalIgnoreCase) ||
            !finalUri.AbsolutePath.StartsWith("/news/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("剧情攻略来源地址不受支持。");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("剧情攻略响应超过大小限制。");

        var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (payload.Length == 0 || payload.Length > MaximumResponseBytes)
            throw new InvalidDataException("剧情攻略响应为空或超过大小限制。");
        return Encoding.UTF8.GetString(payload);
    }

    private static StoryGuideDetail ParseEftarkov(StoryGuideChapter chapter, string html)
    {
        if (string.IsNullOrWhiteSpace(html)) throw new InvalidDataException("逃离塔科夫中文 Wiki 剧情攻略正文为空。");
        var articleStart = html.IndexOf("<article id=\"newsContent\"", StringComparison.OrdinalIgnoreCase);
        if (articleStart < 0) throw new InvalidDataException("逃离塔科夫中文 Wiki 正文结构已变化。");
        var articleEnd = html.IndexOf("</article>", articleStart, StringComparison.OrdinalIgnoreCase);
        var rendered = articleEnd > articleStart ? html[articleStart..articleEnd] : html[articleStart..];
        var guideMarker = rendered.IndexOf("class=\"rwgl\"", StringComparison.OrdinalIgnoreCase);
        var requirements = ReadEftarkovRequirements(rendered, guideMarker);
        if (guideMarker >= 0) rendered = rendered[guideMarker..];

        var markers = EftStepMarkerPattern.Matches(rendered);
        var steps = new List<StoryGuideStep>(markers.Count);
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            var title = StripMarkup(marker.Groups["title"].Value);
            if (title.Length == 0 || title.Contains("相关评论", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsGameModeSplitMarker(title)) continue;
            var start = marker.Index;
            var next = index + 1 < markers.Count ? markers[index + 1].Index : rendered.Length;
            var block = rendered[start..next];
            var images = ReadImages(block, null, title);
            var imageTitles = images.Select(image => image.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notes = ExtractNarrative(block)
                .Where(text => !IsEquivalentText(text, title) && !imageTitles.Contains(text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            steps.Add(new StoryGuideStep(steps.Count + 1, title, notes, images, ReadGameMode(title)));
        }

        if (steps.Count == 0) throw new InvalidDataException("逃离塔科夫中文 Wiki 没有可读取的流程步骤。");
        return new StoryGuideDetail(chapter, requirements, steps);
    }

    private static StoryGuideDetail FilterForGameMode(StoryGuideDetail detail, string gameMode)
    {
        var normalizedMode = string.Equals(gameMode, "pve", StringComparison.OrdinalIgnoreCase) ? "pve" : "pvp";
        var steps = detail.Steps
            .Where(step => step.GameMode is null || string.Equals(step.GameMode, normalizedMode, StringComparison.Ordinal))
            .Select((step, index) => step with { Number = index + 1 })
            .ToArray();
        return detail with { Steps = steps };
    }

    private static bool IsGameModeSplitMarker(string title) =>
        title.Contains("PVP", StringComparison.OrdinalIgnoreCase) &&
        title.Contains("PVE", StringComparison.OrdinalIgnoreCase) &&
        (title.Contains("不一样", StringComparison.Ordinal) || title.Contains("不同", StringComparison.Ordinal));

    private static string? ReadGameMode(string title)
    {
        if (title.Contains("（PVE）", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("(PVE)", StringComparison.OrdinalIgnoreCase)) return "pve";
        if (title.Contains("（PVP）", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("(PVP)", StringComparison.OrdinalIgnoreCase)) return "pvp";
        return null;
    }

    private static IReadOnlyList<string> ReadEftarkovRequirements(string html, int guideMarker)
    {
        var marker = html.IndexOf("class=\"rwyq\"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return [];
        var end = guideMarker > marker ? guideMarker : html.Length;
        return ExtractNarrative(html[marker..end])
            .Where(text => text.Length > 0 &&
                           !text.Contains("特别注意", StringComparison.OrdinalIgnoreCase) &&
                           !text.Contains("注意观看", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEquivalentText(string left, string right)
    {
        var normalizedLeft = NormalizeText(left);
        var normalizedRight = NormalizeText(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0) return false;
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)) return true;
        var shorter = Math.Min(normalizedLeft.Length, normalizedRight.Length);
        return shorter >= 6 &&
               (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) ||
                normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal));
    }

    private static string NormalizeText(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyList<string> ExtractNarrative(string html)
    {
        var pattern = new Regex(
            "<(?:li|p)\\b[^>]*>(?<text>[\\s\\S]*?)</(?:li|p)>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);
        return pattern.Matches(html)
            .Select(match => StripMarkup(match.Groups["text"].Value))
            .Where(text => text.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<StoryGuideImage> ReadImages(string html, Uri? baseUri, string fallbackTitle)
    {
        var images = new List<StoryGuideImage>();
        foreach (Match match in ImagePattern.Matches(html))
        {
            var attributes = match.Groups["attrs"].Value;
            var url = ReadAttribute(attributes, "src");
            if (url.Length == 0) continue;
            if (baseUri is not null && Uri.TryCreate(baseUri, url, out var absoluteUri)) url = absoluteUri.AbsoluteUri;
            var title = ReadAttribute(attributes, "alt");
            if (title.Length == 0) title = $"{fallbackTitle} - 图片 {images.Count + 1}";
            var image = new StoryGuideImage(title, url, CreateCacheId(url));
            if (UtilityImageReference.IsSupported(image.ImageCacheId, image.ImageUrl)) images.Add(image);
        }
        return images.DistinctBy(image => image.ImageUrl, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ReadAttribute(string attributes, string name)
    {
        var pattern = new Regex(
            $"(?:^|\\s){name}\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);
        var match = pattern.Match(attributes);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["double"].Success ? match.Groups["double"].Value : match.Groups["single"].Value).Trim()
            : "";
    }

    private static string StripMarkup(string value)
    {
        var withoutSvg = SvgPattern.Replace(value, " ");
        var withBreaks = BreakPattern.Replace(withoutSvg, " ");
        return WhitespacePattern.Replace(WebUtility.HtmlDecode(TagPattern.Replace(withBreaks, " ")), " ").Trim();
    }

    private static string CreateCacheId(string url)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static bool TryGetCached(string id, out StoryGuideDetail? detail)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(id, out var cached))
            {
                detail = null;
                return false;
            }

            var node = CacheNodes[id];
            CacheOrder.Remove(node);
            CacheOrder.AddFirst(node);
            detail = cached;
            return true;
        }
    }

    private static void CacheDetail(string cacheKey, StoryGuideDetail detail)
    {
        lock (CacheGate)
        {
            RemoveCached(cacheKey);
            Cache[cacheKey] = detail;
            CacheNodes[cacheKey] = CacheOrder.AddFirst(cacheKey);
            while (Cache.Count > MaximumCachedChapters && CacheOrder.Last is { } oldest)
                RemoveCached(oldest.Value);
        }
    }

    private static void RemoveCached(string id)
    {
        Cache.Remove(id);
        if (!CacheNodes.Remove(id, out var node)) return;
        CacheOrder.Remove(node);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 2,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/story-guide");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    private static StoryGuideVariant CreateVariant(string id, string name, string articleId) =>
        new(id, name, $"https://www.eftarkov.com/news/{articleId}.html");
}

internal sealed record StoryGuideChapter(string Id, string Name, string EnglishName, string Category, string Summary);

internal sealed record StoryGuideVariant(string Id, string Name, string SourceUrl);

internal sealed record StoryGuideDetail(
    StoryGuideChapter Chapter,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<StoryGuideStep> Steps);

internal sealed record StoryGuideStep(
    int Number,
    string Title,
    IReadOnlyList<string> Notes,
    IReadOnlyList<StoryGuideImage> Images,
    string? GameMode = null);

internal sealed record StoryGuideImage(string Title, string ImageUrl, string ImageCacheId);

internal sealed record StoryGuideLoadResult(StoryGuideDetail? Detail, string? ErrorMessage, bool UsedCachedFallback = false)
{
    public bool IsAvailable => Detail is not null;
}
