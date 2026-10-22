using System.Globalization;
using System.Text.Json;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Pantry;

/// <summary>
/// Recipes and the meal plan. Planning a recipe checks the pantry and puts what's missing on the
/// shopping list; marking a meal cooked takes its ingredients out of stock.
/// </summary>
/// <remarks>
/// An ingredient is matched to stock by name. Amounts are compared only when both are in the same
/// unit (or both have none); otherwise any stock of it counts as having it.
/// </remarks>
public sealed class MealRepository(SqliteConnection connection, PantryRepository pantry)
{
    public const int MaxIngredients = 60;

    public Recipe AddRecipe(Recipe recipe)
    {
        recipe = Validated(recipe);
        Execute("INSERT INTO recipes (id, household_id, name, servings, instructions, ingredients, tier, created_at) VALUES ($id, $household, $name, $servings, $instructions, $ingredients, $tier, $created)",
            [.. RecipeParameters(recipe), ("$created", Text(DateTimeOffset.UtcNow))]);
        return recipe;
    }

    public Recipe UpdateRecipe(Recipe recipe)
    {
        recipe = Validated(recipe);
        var changed = Execute("UPDATE recipes SET name = $name, servings = $servings, instructions = $instructions, ingredients = $ingredients, tier = $tier WHERE id = $id",
            RecipeParameters(recipe));
        return changed == 0 ? throw new ArgumentException($"There is no recipe {recipe.Id}.", nameof(recipe)) : recipe;
    }

    /// <summary>Deletes a recipe. Meals already planned from it keep their name.</summary>
    public void DeleteRecipe(Guid id)
    {
        Execute("UPDATE planned_meals SET recipe_id = NULL WHERE recipe_id = $id", ("$id", id.ToString()));
        Execute("DELETE FROM recipes WHERE id = $id", ("$id", id.ToString()));
    }

    public Recipe? GetRecipe(Guid id) => QueryRecipes("SELECT id, household_id, name, servings, instructions, ingredients, tier FROM recipes WHERE id = $id",
        ("$id", id.ToString())).SingleOrDefault();

    public IReadOnlyList<Recipe> Recipes(Guid householdId) => QueryRecipes(
        "SELECT id, household_id, name, servings, instructions, ingredients, tier FROM recipes WHERE household_id = $household AND tier <= $shared ORDER BY name COLLATE NOCASE",
        ("$household", householdId.ToString()), ("$shared", (int)PrivacyTier.Household));

    /// <summary>What of a recipe isn't in stock (or not enough of), as shopping lines.</summary>
    public IReadOnlyList<ShoppingItem> Missing(Recipe recipe)
    {
        var stock = pantry.Stock(recipe.HouseholdId);
        var missing = new List<ShoppingItem>();
        foreach (var ingredient in recipe.Ingredients)
        {
            var match = stock.FirstOrDefault(item => string.Equals(item.Name, ingredient.Name, StringComparison.CurrentCultureIgnoreCase));
            if (match is null)
            {
                missing.Add(new ShoppingItem { HouseholdId = recipe.HouseholdId, Name = ingredient.Name, Amount = ingredient.Amount, Unit = ingredient.Unit });
            }
            else if (SameUnit(match.Unit, ingredient.Unit) && ingredient.Amount is { } needed ? match.Amount < needed : match.Amount <= 0)
            {
                var shortBy = ingredient.Amount is { } amount && SameUnit(match.Unit, ingredient.Unit) ? amount - match.Amount : (decimal?)null;
                missing.Add(new ShoppingItem
                {
                    HouseholdId = recipe.HouseholdId, Name = match.Name, Amount = shortBy, Unit = shortBy is null ? null : match.Unit ?? ingredient.Unit,
                    StockItemId = match.Id,
                });
            }
        }

        return missing;
    }

    /// <summary>
    /// Puts a meal on the plan. A recipe's missing ingredients go on the shopping list (each once);
    /// returns the lines added.
    /// </summary>
    public (PlannedMeal Meal, IReadOnlyList<ShoppingItem> AddedToList) Plan(PlannedMeal meal)
    {
        var title = meal.Title?.Trim() ?? "";
        Recipe? recipe = null;
        if (meal.RecipeId is { } recipeId)
        {
            recipe = GetRecipe(recipeId) ?? throw new ArgumentException("That recipe doesn't exist.", nameof(meal));
            title = recipe.Name;
        }

        if (title.Length is 0 or > PantryRepository.MaxNameLength)
        {
            throw new ArgumentException("Say what the meal is, or pick a recipe.", nameof(meal));
        }

        meal = meal with { Title = title, CookedAt = null, Tier = meal.Tier < PrivacyTier.Household ? PrivacyTier.Household : meal.Tier };
        var before = pantry.ShoppingList(meal.HouseholdId).Select(line => line.Id).ToHashSet();
        using var transaction = connection.BeginTransaction();
        Execute("INSERT INTO planned_meals (id, household_id, day, meal, recipe_id, title, cooked_at, tier) VALUES ($id, $household, $day, $meal, $recipe, $title, NULL, $tier)",
            ("$id", meal.Id.ToString()), ("$household", meal.HouseholdId.ToString()), ("$day", meal.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$meal", meal.Meal.ToString()), ("$recipe", meal.RecipeId?.ToString()), ("$title", meal.Title), ("$tier", (int)meal.Tier));

        var added = new List<ShoppingItem>();
        foreach (var line in recipe is null ? [] : Missing(recipe))
        {
            var onList = pantry.AddToList(line);
            if (!before.Contains(onList.Id))
            {
                added.Add(onList);
            }
        }

        transaction.Commit();
        return (meal, added);
    }

    /// <summary>The plan for some days, by day then meal.</summary>
    public IReadOnlyList<PlannedMeal> Meals(Guid householdId, DateOnly from, DateOnly to)
    {
        using var command = Command(
            "SELECT id, day, meal, recipe_id, title, cooked_at, tier FROM planned_meals WHERE household_id = $household AND day >= $from AND day <= $to AND tier <= $shared",
            ("$household", householdId.ToString()), ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$shared", (int)PrivacyTier.Household));
        using var reader = command.ExecuteReader();
        var meals = new List<PlannedMeal>();
        while (reader.Read())
        {
            meals.Add(new PlannedMeal
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = householdId,
                Day = DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Meal = Enum.Parse<Meal>(reader.GetString(2)),
                RecipeId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                Title = reader.GetString(4),
                CookedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Tier = (PrivacyTier)reader.GetInt32(6),
            });
        }

        return meals.OrderBy(meal => meal.Day).ThenBy(meal => meal.Meal).ToList();
    }

    /// <summary>Marks a meal cooked and takes its recipe's ingredients out of stock (where the units match). Only once.</summary>
    public void Cook(Guid mealId, DateTimeOffset at)
    {
        var meal = FindMeal(mealId) ?? throw new ArgumentException($"There is no planned meal {mealId}.", nameof(mealId));
        if (meal.CookedAt is not null)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        if (meal.RecipeId is { } recipeId && GetRecipe(recipeId) is { } recipe)
        {
            var stock = pantry.Stock(meal.HouseholdId);
            foreach (var ingredient in recipe.Ingredients)
            {
                if (stock.FirstOrDefault(item => string.Equals(item.Name, ingredient.Name, StringComparison.CurrentCultureIgnoreCase)) is { } item
                    && SameUnit(item.Unit, ingredient.Unit))
                {
                    pantry.Use(item.Id, ingredient.Amount ?? 1);
                }
            }
        }

        Execute("UPDATE planned_meals SET cooked_at = $at WHERE id = $id", ("$id", mealId.ToString()), ("$at", Text(at)));
        transaction.Commit();
    }

    public void RemoveMeal(Guid mealId) => Execute("DELETE FROM planned_meals WHERE id = $id", ("$id", mealId.ToString()));

    private PlannedMeal? FindMeal(Guid mealId)
    {
        using var command = Command("SELECT household_id FROM planned_meals WHERE id = $id", ("$id", mealId.ToString()));
        return command.ExecuteScalar() is string household
            ? Meals(Guid.Parse(household), DateOnly.MinValue, DateOnly.MaxValue).FirstOrDefault(meal => meal.Id == mealId)
            : null;
    }

    private static bool SameUnit(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static Recipe Validated(Recipe recipe)
    {
        var name = recipe.Name?.Trim() ?? "";
        if (name.Length is 0 or > PantryRepository.MaxNameLength)
        {
            throw new ArgumentException("Give the recipe a name.", nameof(recipe));
        }

        var ingredients = (recipe.Ingredients ?? []).Where(ingredient => !string.IsNullOrWhiteSpace(ingredient.Name))
            .Select(ingredient => ingredient with { Name = ingredient.Name.Trim() }).ToList();
        if (ingredients.Count > MaxIngredients || ingredients.Any(ingredient => ingredient.Name.Length > PantryRepository.MaxNameLength || ingredient.Amount is < 0))
        {
            throw new ArgumentException($"A recipe has at most {MaxIngredients} ingredients, each with a short name.", nameof(recipe));
        }

        return recipe with
        {
            Name = name,
            Ingredients = ingredients,
            Servings = recipe.Servings is < 1 ? null : recipe.Servings,
            Instructions = string.IsNullOrWhiteSpace(recipe.Instructions) ? null : recipe.Instructions.Trim(),
            Tier = recipe.Tier < PrivacyTier.Household ? PrivacyTier.Household : recipe.Tier,
        };
    }

    private sealed record StoredIngredient(string Name, string? Amount, string? Unit);

    private static (string, object?)[] RecipeParameters(Recipe recipe) =>
    [
        ("$id", recipe.Id.ToString()), ("$household", recipe.HouseholdId.ToString()), ("$name", recipe.Name), ("$servings", recipe.Servings),
        ("$instructions", recipe.Instructions), ("$tier", (int)recipe.Tier),
        ("$ingredients", JsonSerializer.Serialize(recipe.Ingredients.Select(i =>
            new StoredIngredient(i.Name, i.Amount?.ToString(CultureInfo.InvariantCulture), i.Unit)).ToList())),
    ];

    private List<Recipe> QueryRecipes(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var recipes = new List<Recipe>();
        while (reader.Read())
        {
            var stored = JsonSerializer.Deserialize<List<StoredIngredient>>(reader.GetString(5)) ?? [];
            recipes.Add(new Recipe
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                Name = reader.GetString(2),
                Servings = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Instructions = reader.IsDBNull(4) ? null : reader.GetString(4),
                Ingredients = stored.Select(i => new Ingredient(i.Name,
                    i.Amount is null ? null : decimal.Parse(i.Amount, NumberStyles.Number, CultureInfo.InvariantCulture), i.Unit)).ToList(),
                Tier = (PrivacyTier)reader.GetInt32(6),
            });
        }

        return recipes;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Text(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
