using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Downloads and decodes market thumbnails without allowing a newly realised
/// virtualised list to overload the asset CDN with dozens of parallel requests.
/// The original on-disk icon cache remains shared with the legacy tool.
/// </summary>
public static class ItemIconService
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private const int MaxMemoryCacheEntries = 160;
    private const long MaxMemoryCacheBytes = 64L * 1024 * 1024;
    private const int MaxDiskCacheEntries = 6000;
    private const long MaxDiskCacheBytes = 160L * 1024 * 1024;
    private const int MaxConcurrentDownloads = 4;
    private const int MaxDownloadAttempts = 3;
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim DownloadGate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Pending = new(StringComparer.Ordinal);
    private static readonly object MemoryCacheGate = new();
    private static readonly Dictionary<string, BitmapSource> MemoryCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> MemoryCacheSizes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LinkedListNode<string>> MemoryCacheNodes = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> MemoryCacheOrder = [];
    private static long _memoryCacheBytes;
    private static int _memoryCacheGeneration;
    private static int _diskCacheCleanupQueued;
    private static readonly string IconDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocator",
        "market-icons");

    /// <summary>
    /// Loads a thumbnail from the disk cache or CDN. Full workbench artwork is
    /// intentionally cached separately from compact list icons for the same item.
    /// </summary>
    public static async Task<BitmapSource?> GetAsync(
        string itemId,
        string? iconLink,
        string? fallbackIconLink = null,
        bool preferFullImage = false)
    {
        var normalizedId = NormalizeId(itemId);
        if (normalizedId is null) return null;
        var cacheKey = preferFullImage ? $"{normalizedId}-full" : normalizedId;
        if (TryGetMemoryCached(cacheKey, out var cached)) return cached;

        var iconPath = Path.Combine(IconDirectory, $"{cacheKey}.webp");
        var candidates = BuildCandidateUris(iconLink, fallbackIconLink);
        if (!File.Exists(iconPath) && candidates.Count == 0) return null;

        var cacheGeneration = Volatile.Read(ref _memoryCacheGeneration);
        var task = Pending.GetOrAdd(cacheKey, _ => Task.Run(() => LoadAsync(cacheKey, candidates)));
        try
        {
            var source = await task.ConfigureAwait(false);
            if (source is not null && cacheGeneration == Volatile.Read(ref _memoryCacheGeneration))
                CacheInMemory(cacheKey, source);
            return source;
        }
        finally
        {
            if (task.IsCompleted && Pending.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, task))
                Pending.TryRemove(cacheKey, out _);
        }
    }

    public static void ReleaseMemory()
    {
        Interlocked.Increment(ref _memoryCacheGeneration);
        lock (MemoryCacheGate)
        {
            MemoryCache.Clear();
            MemoryCacheSizes.Clear();
            MemoryCacheNodes.Clear();
            MemoryCacheOrder.Clear();
            _memoryCacheBytes = 0;
        }
    }

    private static bool TryGetMemoryCached(string itemId, out BitmapSource? source)
    {
        lock (MemoryCacheGate)
        {
            if (!MemoryCache.TryGetValue(itemId, out var cached))
            {
                source = null;
                return false;
            }

            var node = MemoryCacheNodes[itemId];
            MemoryCacheOrder.Remove(node);
            MemoryCacheOrder.AddFirst(node);
            source = cached;
            return true;
        }
    }

    private static void CacheInMemory(string itemId, BitmapSource source)
    {
        lock (MemoryCacheGate)
        {
            var decodedBytes = EstimateDecodedBytes(source);
            if (MemoryCacheNodes.TryGetValue(itemId, out var existingNode))
            {
                _memoryCacheBytes -= MemoryCacheSizes[itemId];
                MemoryCache[itemId] = source;
                MemoryCacheSizes[itemId] = decodedBytes;
                _memoryCacheBytes += decodedBytes;
                MemoryCacheOrder.Remove(existingNode);
                MemoryCacheOrder.AddFirst(existingNode);
            }
            else
            {
                var node = MemoryCacheOrder.AddFirst(itemId);
                MemoryCacheNodes[itemId] = node;
                MemoryCache[itemId] = source;
                MemoryCacheSizes[itemId] = decodedBytes;
                _memoryCacheBytes += decodedBytes;
            }

            while ((MemoryCache.Count > MaxMemoryCacheEntries || _memoryCacheBytes > MaxMemoryCacheBytes) &&
                   MemoryCacheOrder.Last is { } leastRecent)
            {
                var key = leastRecent.Value;
                MemoryCacheOrder.RemoveLast();
                MemoryCacheNodes.Remove(key);
                MemoryCache.Remove(key);
                if (MemoryCacheSizes.Remove(key, out var removedBytes))
                    _memoryCacheBytes -= removedBytes;
            }
        }
    }

    private static long EstimateDecodedBytes(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return 0;
        // WPF commonly expands WebP/PNG thumbnails to 32-bit pixels. Use four
        // bytes as a conservative floor even when the reported source format
        // is indexed, otherwise a cache of large workbench images can grow far
        // beyond the entry-count limit.
        var bytesPerPixel = Math.Max(4, (source.Format.BitsPerPixel + 7) / 8);
        return (long)source.PixelWidth * source.PixelHeight * bytesPerPixel;
    }

    private static async Task<BitmapSource?> LoadAsync(string itemId, IReadOnlyList<Uri> candidates)
    {
        var iconPath = Path.Combine(IconDirectory, $"{itemId}.webp");
        if (File.Exists(iconPath))
        {
            var cached = TryDecodeIconFile(iconPath);
            if (cached is not null) return cached;
            DeleteInvalidIcon(iconPath);
        }

        Exception? lastFailure = null;
        foreach (var uri in candidates)
        {
            try
            {
                var payload = await DownloadIconAsync(uri).ConfigureAwait(false);
                var bitmap = DecodeIcon(payload);
                try
                {
                    await SaveIconAsync(iconPath, payload).ConfigureAwait(false);
                    QueueDiskCacheCleanup();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogService.Warning("物品图片", "图片已下载但无法写入磁盘缓存", $"物品: {itemId}\n{exception.Message}");
                }
                return bitmap;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        if (lastFailure is not null)
            RuntimeLogService.Warning("物品图片", "加载物品图标失败", $"物品: {itemId}\n{lastFailure}");
        return null;
    }

    private static async Task<byte[]> DownloadIconAsync(Uri uri)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            await DownloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                        null,
                        response.StatusCode);
                    if (!IsTransient(response.StatusCode) || attempt == MaxDownloadAttempts) throw failure;
                    lastFailure = failure;
                }
                else
                {
                    if (response.Content.Headers.ContentLength is > MaxIconBytes)
                        throw new InvalidDataException("图标文件超过大小限制。");

                    var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (payload.Length == 0 || payload.Length > MaxIconBytes)
                        throw new InvalidDataException("图标内容为空或超过大小限制。");
                    return payload;
                }
            }
            catch (Exception exception) when (attempt < MaxDownloadAttempts && IsTransient(exception))
            {
                lastFailure = exception;
            }
            finally
            {
                DownloadGate.Release();
            }

            if (attempt < MaxDownloadAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(180 * attempt + Random.Shared.Next(60, 180))).ConfigureAwait(false);
        }

        throw lastFailure ?? new HttpRequestException("图标下载未完成。");
    }

    private static async Task SaveIconAsync(string iconPath, byte[] payload)
    {
        Directory.CreateDirectory(IconDirectory);
        var temporaryPath = $"{iconPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload).ConfigureAwait(false);
            File.Move(temporaryPath, iconPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("物品图片", "清理图片临时文件失败", exception.Message);
            }
        }
    }

    private static IReadOnlyList<Uri> BuildCandidateUris(string? iconLink, string? fallbackIconLink)
    {
        var result = new List<Uri>(2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in new[] { iconLink, fallbackIconLink })
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !seen.Add(uri.AbsoluteUri))
                continue;
            result.Add(uri);
        }

        return result;
    }

    private static BitmapSource? TryDecodeIconFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return DecodeIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource DecodeIcon(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return DecodeIcon(stream);
    }

    private static BitmapSource DecodeIcon(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // This is a stream-only image, not a URI image. IgnoreImageCache causes
        // current WPF to remove a null URI from its image cache and throws before
        // the WebP decoder returns a frame.
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void DeleteInvalidIcon(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("物品图片", "删除损坏图片缓存失败", exception.Message);
        }
    }

    private static void QueueDiskCacheCleanup()
    {
        if (Interlocked.Exchange(ref _diskCacheCleanupQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(IconDirectory)) return;
                var files = Directory.EnumerateFiles(IconDirectory, "*.webp", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderBy(info => info.LastWriteTimeUtc)
                    .ToList();
                var totalBytes = files.Sum(info => info.Length);
                while (files.Count > MaxDiskCacheEntries || totalBytes > MaxDiskCacheBytes)
                {
                    var oldest = files[0];
                    files.RemoveAt(0);
                    totalBytes -= oldest.Length;
                    try { oldest.Delete(); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        RuntimeLogService.Warning("物品图片", "回收旧图片缓存失败", exception.Message);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("物品图片", "检查图片缓存容量失败", exception.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _diskCacheCleanupQueued, 0);
            }
        });
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } statusCode } ? IsTransient(statusCode) :
        exception is HttpRequestException or TaskCanceledException or IOException;

    private static string? NormalizeId(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 24 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = MaxConcurrentDownloads,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/market");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        return client;
    }
}
