namespace TarkovMapLocator.ModuleContracts;

public static class FeatureRoutes
{
    public const string Market = "market";
    public const string TaskItems = "task-items";
    public const string Memo = "memo";
    public const string TaskTracking = "task-tracking";
    public const string TeamSync = "team-sync";
    public const string MobileMap = "mobile-map";
    public const string ScreenFilter = "screen-filter";
    public const string InGamePrice = "in-game-price";
    public const string Utilities = "utilities";

    public static IReadOnlyList<string> All { get; } =
    [
        Market,
        TaskItems,
        Memo,
        TaskTracking,
        InGamePrice,
        ScreenFilter,
        TeamSync,
        MobileMap,
        Utilities
    ];
}
