using System.Net.Http.Json;
using TarkovMapLocator.App.Models;

namespace TarkovMapLocator.App.Services;

public sealed class FleaMarketPriceService
{
    private static readonly Uri BackendBaseUri = new("http://127.0.0.1:5173/");
    private readonly HttpClient httpClient = new()
    {
        BaseAddress = BackendBaseUri,
        Timeout = TimeSpan.FromSeconds(40)
    };

    public async Task<MarketStateSnapshot?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<MarketStateSnapshot>(
            "api/market/state",
            MarketJsonSerializerContext.Default.MarketStateSnapshot,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FleaMarketSearchResult?> SearchAsync(
        string mode,
        string query,
        int limit = 80,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? "pve" : "pvp";
        var safeLimit = Math.Clamp(limit, 1, 200);
        var uri = $"api/market/search?mode={normalizedMode}&q={Uri.EscapeDataString(query.Trim())}&limit={safeLimit}";
        return await httpClient.GetFromJsonAsync<FleaMarketSearchResult>(
            uri,
            MarketJsonSerializerContext.Default.FleaMarketSearchResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketStateSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync("api/market/refresh", null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketStateSnapshot>(
            MarketJsonSerializerContext.Default.MarketStateSnapshot,
            cancellationToken).ConfigureAwait(false);
    }

    public Uri GetIconUri(string itemId)
    {
        return new Uri(BackendBaseUri, $"api/market/icon/{Uri.EscapeDataString(itemId)}.webp");
    }
}
