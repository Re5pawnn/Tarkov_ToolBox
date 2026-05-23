using System.Text.Json.Serialization;

namespace TarkovMapLocator.App.Models;

public sealed record TaskItemTrackerState(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("updatedAt")] double? UpdatedAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("counts")] TaskItemTrackerCounts? Counts);

public sealed record TaskItemSearchResult(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("updatedAt")] double? UpdatedAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<TaskItemRow> Items,
    [property: JsonPropertyName("totals")] TaskItemTotals? Totals);

public sealed record TaskItemRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shortName")] string ShortName,
    [property: JsonPropertyName("iconLink")] string? IconLink,
    [property: JsonPropertyName("required")] int Required,
    [property: JsonPropertyName("completedRequired")] int CompletedRequired,
    [property: JsonPropertyName("completedTaskRequired")] int CompletedTaskRequired,
    [property: JsonPropertyName("completedHideoutRequired")] int CompletedHideoutRequired,
    [property: JsonPropertyName("foundInRaidRequired")] int FoundInRaidRequired,
    [property: JsonPropertyName("taskRequired")] int TaskRequired,
    [property: JsonPropertyName("hideoutRequired")] int HideoutRequired,
    [property: JsonPropertyName("have")] int Have,
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("fleaPrice")] long? FleaPrice,
    [property: JsonPropertyName("avg24hPrice")] long? Avg24hPrice,
    [property: JsonPropertyName("estimatedCost")] long? EstimatedCost,
    [property: JsonPropertyName("sources")] IReadOnlyList<TaskItemSource> Sources,
    [property: JsonPropertyName("choice")] bool Choice);

public sealed record TaskItemSource(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("foundInRaid")] bool FoundInRaid,
    [property: JsonPropertyName("trader")] string Trader,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("choice")] bool Choice);

public sealed record TaskItemTotals(
    [property: JsonPropertyName("required")] int Required,
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("estimatedCost")] long EstimatedCost);

public sealed record TaskItemTrackerCounts(
    [property: JsonPropertyName("pvp")] int Pvp,
    [property: JsonPropertyName("pve")] int Pve);

public sealed record TaskItemUpdateResult(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("sourceKey")] string? SourceKey,
    [property: JsonPropertyName("have")] int? Have,
    [property: JsonPropertyName("completed")] bool? Completed,
    [property: JsonPropertyName("error")] string? Error);
