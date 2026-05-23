using TarkovMapLocator.App.Models;

namespace TarkovMapLocator.App.Services;

public sealed class MapImageCacheService
{
    private static readonly string[] PrototypeMapKeys =
    [
        "customs",
        "factory",
        "ground-zero",
        "interchange",
        "the-lab",
        "the-labyrinth",
        "lighthouse",
        "reserve",
        "shoreline",
        "streets-of-tarkov",
        "woods"
    ];

    private static readonly string[] CacheImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF];

    private readonly string appDirectory;

    public MapImageCacheService()
        : this(AppContext.BaseDirectory)
    {
    }

    public MapImageCacheService(string appDirectory)
    {
        this.appDirectory = appDirectory;
    }

    public MapCacheCheckResult CheckPrototypeMapImages(IReadOnlyList<MapPrototypeMetadata> maps)
    {
        var targetMaps = maps
            .Where(map => PrototypeMapKeys.Contains(map.Key, StringComparer.OrdinalIgnoreCase))
            .GroupBy(map => map.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var ready = 0;
        var missing = 0;
        foreach (var map in targetMaps)
        {
            if (FindUsableCachedImagePath(map.Key) is not null)
            {
                ready++;
            }
            else
            {
                missing++;
            }
        }

        var status = missing == 0
            ? $"原生地图图片已就绪: {ready}"
            : $"缺少 {missing} 张预渲染地图图片，请重新生成发布资源。";
        return new MapCacheCheckResult(ready, missing, missing > 0, status);
    }

    public static bool IsUsableCachedImage(string cacheImagePath)
    {
        if (string.IsNullOrWhiteSpace(cacheImagePath) || !File.Exists(cacheImagePath))
        {
            return false;
        }

        var fileInfo = new FileInfo(cacheImagePath);
        return fileInfo.Length > 0 && HasSupportedImageHeader(cacheImagePath);
    }

    private string? FindUsableCachedImagePath(string key)
    {
        var cacheDirectory = Path.Combine(appDirectory, "assets", "maps", "native-cache");
        var cacheName = NormalizeCacheName(key);
        foreach (var extension in CacheImageExtensions)
        {
            var path = Path.Combine(cacheDirectory, cacheName + extension);
            if (IsUsableCachedImage(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool HasSupportedImageHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[12];
            var bytesRead = stream.Read(buffer);
            return HasPngHeader(buffer[..bytesRead]) ||
                HasJpegHeader(buffer[..bytesRead]) ||
                HasWebpHeader(buffer[..bytesRead]);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPngHeader(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= PngHeader.Length && buffer[..PngHeader.Length].SequenceEqual(PngHeader);

    private static bool HasJpegHeader(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= JpegHeader.Length && buffer[..JpegHeader.Length].SequenceEqual(JpegHeader);

    private static bool HasWebpHeader(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 12 &&
        buffer[0] == (byte)'R' &&
        buffer[1] == (byte)'I' &&
        buffer[2] == (byte)'F' &&
        buffer[3] == (byte)'F' &&
        buffer[8] == (byte)'W' &&
        buffer[9] == (byte)'E' &&
        buffer[10] == (byte)'B' &&
        buffer[11] == (byte)'P';

    private static string NormalizeCacheName(string key)
    {
        var normalized = new string(key
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "map" : normalized;
    }
}
