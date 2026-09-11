namespace TarkovMapLocator.Modules.Utilities;

internal static class UtilityImageReference
{
    private static readonly HashSet<string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.kaedeori.com",
        "img.eftarkov.com",
        "www.optarkov.com"
    };

    public static bool IsSupported(string imageId, string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageId) || imageId.Length > 128 ||
            imageId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !TrustedHosts.Contains(uri.Host))
            return false;

        var path = uri.AbsolutePath;
        if (uri.Host.Equals("img.eftarkov.com", StringComparison.OrdinalIgnoreCase))
            return path.Contains("/upFiles/infoImg/", StringComparison.OrdinalIgnoreCase);
        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
