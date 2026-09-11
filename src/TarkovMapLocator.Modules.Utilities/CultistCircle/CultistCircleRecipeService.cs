using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TarkovMapLocator.Modules.Utilities.CultistCircle;

internal static class CultistCircleRecipeService
{
    private const string ResourceName = "TarkovMapLocator.Modules.Utilities.Resources.CultistCircleRecipes.json";
    private static readonly Lazy<CultistCircleRecipeCatalog> Catalog = new(LoadCore);

    public static CultistCircleRecipeCatalog Load() => Catalog.Value;

    public static IReadOnlyList<CultistCircleRecipe> Search(string query)
    {
        var recipes = Catalog.Value.Recipes;
        var term = query.Trim();
        if (term.Length == 0) return recipes;
        return recipes.Where(recipe =>
                recipe.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                recipe.Inputs.Concat(recipe.RewardGroups.SelectMany(group => group.Items)).Any(item =>
                    item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static void Verify()
    {
        var catalog = Load();
        var sample = catalog.Recipes.FirstOrDefault();
        if (sample is null ||
            catalog.Recipes.Any(recipe => recipe.DurationMinutes <= 0 || recipe.Inputs.Count == 0 || recipe.RewardGroups.Count == 0) ||
            Search(sample.Title).All(recipe => recipe.Title != sample.Title) ||
            catalog.Recipes.SelectMany(recipe => recipe.Inputs.Concat(recipe.RewardGroups.SelectMany(group => group.Items)))
                .Any(item => item.Id.Length != 24 || item.Id.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidDataException("邪教圈配方数据不完整。");
    }

    private static CultistCircleRecipeCatalog LoadCore()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("邪教圈配方数据未打包进小工具组件。");
        var catalog = JsonSerializer.Deserialize<CultistCircleRecipeCatalog>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("邪教圈配方数据无法读取。");
        if (catalog.Recipes.Count == 0 || catalog.Recipes.Any(recipe => recipe.Inputs.Count == 0 || recipe.RewardGroups.Count == 0))
            throw new InvalidDataException("邪教圈配方数据不完整。");
        if (catalog.Recipes.Any(recipe =>
                recipe.Title.Contains('\uFFFD') || recipe.Restriction.Contains('\uFFFD') ||
                recipe.Inputs.Concat(recipe.RewardGroups.SelectMany(group => group.Items)).Any(item =>
                    item.Name.Contains('\uFFFD') || item.EnglishName.Contains('\uFFFD'))))
            throw new InvalidDataException("邪教圈配方文本编码损坏。");
        return catalog;
    }
}

internal sealed class CultistCircleRecipeCatalog
{
    public DateTimeOffset UpdatedAt { get; set; }
    public string SourceUrl { get; set; } = "";
    public List<CultistCircleRecipe> Recipes { get; set; } = [];
}

internal sealed class CultistCircleRecipe
{
    public string Title { get; set; } = "";
    public int DurationMinutes { get; set; }
    public string Restriction { get; set; } = "";
    public bool IsNew { get; set; }
    public List<CultistCircleItem> Inputs { get; set; } = [];
    public List<CultistCircleRewardGroup> RewardGroups { get; set; } = [];
}

internal sealed class CultistCircleRewardGroup
{
    public bool Random { get; set; }
    public List<CultistCircleItem> Items { get; set; } = [];
}

internal sealed class CultistCircleItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public string IconLink { get; set; } = "";
    public int Count { get; set; }
}
