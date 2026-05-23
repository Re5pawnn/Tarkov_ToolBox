using System.Text.Json.Serialization;

namespace TarkovMapLocator.App.Models;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MarketStateSnapshot))]
[JsonSerializable(typeof(FleaMarketSearchResult))]
[JsonSerializable(typeof(TaskItemTrackerState))]
[JsonSerializable(typeof(TaskItemSearchResult))]
[JsonSerializable(typeof(TaskItemUpdateResult))]
internal sealed partial class MarketJsonSerializerContext : JsonSerializerContext;
