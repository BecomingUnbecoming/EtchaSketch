using System.Globalization;
using System.Text.RegularExpressions;
using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Pantry;

/// <summary>A recipe the household cooks: what goes in it, and how. Shared with the household.</summary>
public sealed record Recipe : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required string Name { get; init; }

    [Tier(PrivacyTier.Household)]
    public int? Servings { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Instructions { get; init; }

    [Tier(PrivacyTier.Household)]
    public IReadOnlyList<Ingredient> Ingredients { get; init; } = [];

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;
}

/// <summary>One line of a recipe, like "200 g rice" or "salt".</summary>
public sealed partial record Ingredient(string Name, decimal? Amount = null, string? Unit = null)
{
    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        "g", "kg", "mg", "ml", "l", "cl", "dl", "tsp", "tbsp", "cup", "cups", "oz", "lb", "lbs",
        "pack", "packs", "can", "cans", "tin", "tins", "jar", "jars", "clove", "cloves", "slice", "slices", "pinch", "bunch",
    };

    /// <summary>Reads a line like "200 g rice", "2 eggs", "1.5 l milk" or "salt".</summary>
    public static Ingredient Parse(string line)
    {
        var text = line.Trim();
        var match = Leading().Match(text);
        if (!match.Success)
        {
            return new Ingredient(text);
        }

        var amount = decimal.Parse(match.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture);
        var rest = match.Groups["rest"].Value.Trim();
        var words = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2 && Units.Contains(words[0])
            ? new Ingredient(words[1].Trim(), amount, words[0])
            : new Ingredient(rest, amount);
    }

    public override string ToString() =>
        Amount is { } amount
            ? $"{amount.ToString("0.##", CultureInfo.InvariantCulture)}{(Unit is { } unit ? " " + unit : "")} {Name}"
            : Name;

    [GeneratedRegex(@"^(?<amount>\d+(?:[.,]\d+)?)\s*(?<rest>.+)$")]
    private static partial Regex Leading();
}

public enum Meal
{
    Breakfast,
    Lunch,
    Dinner,
    Snack,
}

/// <summary>A meal on a day: a recipe, or just a name like "Takeaway".</summary>
public sealed record PlannedMeal : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required DateOnly Day { get; init; }

    [Tier(PrivacyTier.Household)]
    public Meal Meal { get; init; } = Meal.Dinner;

    [Tier(PrivacyTier.Household)]
    public Guid? RecipeId { get; init; }

    /// <summary>The recipe's name, or what's planned when there's no recipe.</summary>
    [Tier(PrivacyTier.Household)]
    public required string Title { get; init; }

    /// <summary>When it was cooked (and its ingredients taken out of stock), or null.</summary>
    [Tier(PrivacyTier.Household)]
    public DateTimeOffset? CookedAt { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;
}
