namespace TarkovMapLocator.Modules.Utilities.HideoutProfit;

public enum HideoutProfitSortField { Duration, Profit, ProfitPerHour }

public sealed record HideoutProfitStationOption(string Id, string Name);

public sealed record HideoutProfitIngredient(
    string Id,
    string Name,
    int Count,
    bool IsTool,
    int? UnitPrice,
    string PriceSource,
    string IconLink);

public sealed record HideoutProfitRow(
    string CraftId,
    string StationId,
    string StationName,
    int StationLevel,
    IReadOnlyList<HideoutProfitIngredient> Inputs,
    HideoutProfitIngredient Output,
    int DurationSeconds,
    long? InputCost,
    long? OutputGross,
    long? FleaFee,
    long? Profit,
    long? ProfitPerHour,
    string OutputSource)
{
    public string StationText => $"{StationLevel}级{StationName}";
    public string DurationText => DurationSeconds >= 3600
        ? $"{DurationSeconds / 3600}小时{(DurationSeconds % 3600) / 60:00}分"
        : $"{Math.Max(1, DurationSeconds / 60)}分钟";
    public string InputSummary => string.Join("\n", Inputs.Select(input => input.IsTool
        ? $"{input.Name} ×{input.Count} · 工具（不计成本）"
        : input.UnitPrice is > 0
            ? $"{input.Name} ×{input.Count} · {input.PriceSource}"
            : $"{input.Name} ×{input.Count} · 暂无价格"));
    public string OutputSummary => $"{Output.Name} ×{Output.Count}\n{OutputSource}";
    public string InputCostText => InputCost is { } value ? $"{value:N0} ₽" : "价格不完整";
    public string OutputGrossText => OutputGross is { } value ? $"{value:N0} ₽" : "价格不完整";
    public string FeeText => FleaFee is > 0 ? $"手续费 {FleaFee:N0} ₽" : "无跳蚤手续费";
    public string ProfitText => Profit is { } value ? $"{value:+#,0;-#,0;0} ₽" : "无法计算";
    public string ProfitPerHourText => ProfitPerHour is { } value ? $"{value:+#,0;-#,0;0} ₽/h" : "无法计算";
    public bool IsProfitable => Profit is > 0;
    public bool IsLoss => Profit is < 0;
    public string OutputIconLink => Output.IconLink;
}

public sealed record HideoutProfitLoadResult(
    IReadOnlyList<HideoutProfitRow> Rows,
    IReadOnlyList<HideoutProfitStationOption> Stations,
    DateTimeOffset? UpdatedAt,
    bool IsStale,
    string? ErrorMessage,
    int TotalCrafts,
    int CalculableCrafts);
