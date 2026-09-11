using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TarkovMapLocatorDesktop.Services;

public static class SeasonDocumentImageService
{
    private const int PreviewWidth = 560;
    private const int FullImageWidth = 1920;
    private const int MaxPayloadBytes = 12 * 1024 * 1024;
    private const int MaxRedirects = 4;
    private const int MaxPreviewEntries = 12;
    private const long MaxPreviewBytes = 24L * 1024 * 1024;
    private const int MaxDiskEntries = 480;
    private const long MaxDiskBytes = 384L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim DownloadGate = new(2, 2);
    private static readonly ConcurrentDictionary<string, Task<string>> PendingDownloads = new(StringComparer.Ordinal);
    private static readonly object PreviewCacheGate = new();
    private static readonly Dictionary<string, BitmapSource> PreviewCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> PreviewSizes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LinkedListNode<string>> PreviewNodes = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> PreviewOrder = [];
    private static long _previewBytes;
    private static int _previewCacheGeneration;
    private static int _diskCleanupQueued;
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovMapLocatorDesktop",
        "season-document-images");

    public static Task<BitmapSource?> GetPreviewAsync(string imageId, string imageUrl) =>
        GetAsync(imageId, imageUrl, PreviewWidth, cachePreview: true);

    public static Task<BitmapSource?> GetFullAsync(string imageId, string imageUrl) =>
        GetAsync(imageId, imageUrl, FullImageWidth, cachePreview: false);

    private static async Task<BitmapSource?> GetAsync(string imageId, string imageUrl, int decodeWidth, bool cachePreview)
    {
        var normalizedId = NormalizeId(imageId);
        if (normalizedId is null || !TryValidateUri(imageUrl, out var uri)) return null;
        if (cachePreview && TryGetPreview(normalizedId, out var cached)) return cached;

        var cacheGeneration = Volatile.Read(ref _previewCacheGeneration);
        try
        {
            var path = await EnsureCachedAsync(normalizedId, uri).ConfigureAwait(false);
            var bitmap = await Task.Run(() => TryDecode(path, decodeWidth)).ConfigureAwait(false);
            if (bitmap is null)
            {
                TryDelete(path);
                path = await EnsureCachedAsync(normalizedId, uri).ConfigureAwait(false);
                bitmap = await Task.Run(() => TryDecode(path, decodeWidth)).ConfigureAwait(false);
            }

            if (bitmap is not null && cachePreview && cacheGeneration == Volatile.Read(ref _previewCacheGeneration))
                CachePreview(normalizedId, bitmap);
            return bitmap;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RuntimeLogService.Warning("攻略图片", "图片加载失败", $"图片: {normalizedId}\n{exception.Message}");
            return null;
        }
    }

    public static void ReleaseMemory()
    {
        Interlocked.Increment(ref _previewCacheGeneration);
        lock (PreviewCacheGate)
        {
            PreviewCache.Clear();
            PreviewSizes.Clear();
            PreviewNodes.Clear();
            PreviewOrder.Clear();
            _previewBytes = 0;
        }
    }

    private static bool TryGetPreview(string id, out BitmapSource? source)
    {
        lock (PreviewCacheGate)
        {
            if (!PreviewCache.TryGetValue(id, out var cached))
            {
                source = null;
                return false;
            }

            var node = PreviewNodes[id];
            PreviewOrder.Remove(node);
            PreviewOrder.AddFirst(node);
            source = cached;
            return true;
        }
    }

    private static void CachePreview(string id, BitmapSource source)
    {
        lock (PreviewCacheGate)
        {
            var bytes = EstimateDecodedBytes(source);
            if (PreviewNodes.TryGetValue(id, out var existing))
            {
                _previewBytes -= PreviewSizes[id];
                PreviewCache[id] = source;
                PreviewSizes[id] = bytes;
                _previewBytes += bytes;
                PreviewOrder.Remove(existing);
                PreviewOrder.AddFirst(existing);
            }
            else
            {
                PreviewCache[id] = source;
                PreviewSizes[id] = bytes;
                PreviewNodes[id] = PreviewOrder.AddFirst(id);
                _previewBytes += bytes;
            }

            while ((PreviewCache.Count > MaxPreviewEntries || _previewBytes > MaxPreviewBytes) && PreviewOrder.Last is { } oldest)
            {
                var key = oldest.Value;
                PreviewOrder.RemoveLast();
                PreviewNodes.Remove(key);
                PreviewCache.Remove(key);
                if (PreviewSizes.Remove(key, out var removedBytes)) _previewBytes -= removedBytes;
            }
        }
    }

    private static async Task<string> EnsureCachedAsync(string id, Uri uri)
    {
        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, $"{id}.webp");
        if (File.Exists(path)) return path;

        var task = PendingDownloads.GetOrAdd(id, _ => DownloadAsync(path, uri));
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted && PendingDownloads.TryGetValue(id, out var current) && ReferenceEquals(current, task))
                PendingDownloads.TryRemove(id, out _);
        }
    }

    private static async Task<string> DownloadAsync(string path, Uri uri)
    {
        await DownloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await GetValidatedResponseAsync(uri).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxPayloadBytes)
                throw new InvalidDataException("位置截图超过大小限制。");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is { Length: > 0 } &&
                !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("位置截图响应不是图片。");

            var payload = await ReadBoundedPayloadAsync(response.Content).ConfigureAwait(false);

            var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, payload).ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            QueueDiskCleanup();
            return path;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task<HttpResponseMessage> GetValidatedResponseAsync(Uri uri)
    {
        var current = uri;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !IsAllowedImageUri(current))
                throw new InvalidDataException("位置截图重定向到了不受支持的地址。");

            var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                var finalUri = response.RequestMessage?.RequestUri ?? current;
                if (finalUri.Scheme == Uri.UriSchemeHttps && IsAllowedImageUri(finalUri)) return response;
                response.Dispose();
                throw new InvalidDataException("位置截图最终地址不受支持。");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("位置截图返回了无效重定向。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidDataException("位置截图重定向次数过多。");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadBoundedPayloadAsync(HttpContent content)
    {
        await using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > MaxPayloadBytes)
                throw new InvalidDataException("位置截图内容超过大小限制。");
            destination.Write(buffer, 0, read);
        }

        if (destination.Length == 0) throw new InvalidDataException("位置截图内容为空。");
        return destination.ToArray();
    }

    private static BitmapSource? TryDecode(string path, int decodeWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void QueueDiskCleanup()
    {
        if (Interlocked.Exchange(ref _diskCleanupQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var files = Directory.EnumerateFiles(CacheDirectory, "*.webp")
                    .Select(path => new FileInfo(path))
                    .OrderBy(file => file.LastAccessTimeUtc)
                    .ToList();
                var bytes = files.Sum(file => file.Length);
                while (files.Count > MaxDiskEntries || bytes > MaxDiskBytes)
                {
                    var file = files[0];
                    files.RemoveAt(0);
                    bytes -= file.Length;
                    TryDelete(file.FullName);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RuntimeLogService.Warning("赛季文件", "位置截图缓存清理失败", exception.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _diskCleanupQueued, 0);
            }
        });
    }

    private static bool TryValidateUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            IsAllowedImageUri(parsed))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    internal static bool IsSupportedImageReference(string imageId, string imageUrl) =>
        NormalizeId(imageId) is not null && TryValidateUri(imageUrl, out _);

    private static bool IsAllowedImageUri(Uri uri)
    {
        var isKaedeoriImage = string.Equals(uri.Host, "cdn.kaedeori.com", StringComparison.OrdinalIgnoreCase) &&
                              uri.AbsolutePath.StartsWith("/uploads/tarkov/season-document-images/", StringComparison.OrdinalIgnoreCase);
        var isOpTarkovImage = string.Equals(uri.Host, "www.optarkov.com", StringComparison.OrdinalIgnoreCase) &&
                             uri.AbsolutePath.StartsWith("/images/season/docuuments/", StringComparison.OrdinalIgnoreCase) &&
                             uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        var isEftarkovGuideImage = string.Equals(uri.Host, "img.eftarkov.com", StringComparison.OrdinalIgnoreCase) &&
                                   uri.AbsolutePath.StartsWith("/upFiles/infoImg/", StringComparison.OrdinalIgnoreCase) &&
                                   uri.AbsolutePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        return isKaedeoriImage || isOpTarkovImage || isEftarkovGuideImage;
    }

    private static string? NormalizeId(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length is 24 or 32 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private static long EstimateDecodedBytes(BitmapSource source) =>
        source.PixelWidth <= 0 || source.PixelHeight <= 0 ? 0 : (long)source.PixelWidth * source.PixelHeight * 4;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLogService.Warning("攻略图片", "图片缓存文件无法删除", exception.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 2,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLocator/season-document-preview");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        return client;
    }
}
