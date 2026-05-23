using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using TarkovMapLocator.App.Models;

namespace TarkovMapLocator.App.Services;

public sealed class TaskItemChecklistService
{
    private static readonly Uri BackendBaseUri = new("http://127.0.0.1:5173/");

    private readonly HttpClient httpClient = new()
    {
        BaseAddress = BackendBaseUri,
        Timeout = TimeSpan.FromSeconds(40)
    };

    public async Task<TaskItemTrackerState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync(
            "api/tracker/state",
            MarketJsonSerializerContext.Default.TaskItemTrackerState,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskItemSearchResult?> SearchAsync(
        string mode,
        string query,
        bool includeTasks,
        bool includeHideout,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? "pve" : "pvp";
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var uri = "api/tracker/search"
            + $"?mode={normalizedMode}"
            + $"&q={Uri.EscapeDataString(query.Trim())}"
            + $"&tasks={(includeTasks ? "1" : "0")}"
            + $"&hideout={(includeHideout ? "1" : "0")}"
            + "&completed=1"
            + $"&limit={safeLimit}";
        return await httpClient.GetFromJsonAsync(
            uri,
            MarketJsonSerializerContext.Default.TaskItemSearchResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskItemTrackerState?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync("api/tracker/refresh", null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            MarketJsonSerializerContext.Default.TaskItemTrackerState,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskItemUpdateResult?> UpdateItemHaveAsync(
        string mode,
        string itemId,
        int have,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["mode"] = NormalizeMode(mode),
            ["itemId"] = itemId,
            ["have"] = Math.Max(0, have)
        };
        return await PostMutationAsync("api/tracker/item", payload, cancellationToken);
    }

    public async Task<TaskItemUpdateResult?> UpdateSourceCompletedAsync(
        string mode,
        string sourceKey,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["mode"] = NormalizeMode(mode),
            ["sourceKey"] = sourceKey,
            ["completed"] = completed
        };
        return await PostMutationAsync("api/tracker/source", payload, cancellationToken);
    }

    public Uri GetMarketIconUri(string itemId)
    {
        return new Uri(BackendBaseUri, $"api/market/icon/{Uri.EscapeDataString(itemId)}.webp");
    }

    private async Task<TaskItemUpdateResult?> PostMutationAsync(
        string uri,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync(
            MarketJsonSerializerContext.Default.TaskItemUpdateResult,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(result?.Error ?? response.ReasonPhrase ?? "Tracker update failed.");
        }

        return result;
    }

    private static string NormalizeMode(string mode)
    {
        return string.Equals(mode, "pve", StringComparison.OrdinalIgnoreCase) ? "pve" : "pvp";
    }
}
