using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Tasks;

namespace EtchaSketch.Data.Import;

/// <summary>Everything read from Grocy's API, before anything is changed here.</summary>
public sealed record GrocySnapshot(
    JsonElement Chores,
    JsonElement ChoresDue,
    JsonElement Batteries,
    JsonElement BatteriesDue,
    JsonElement Products,
    JsonElement Stock,
    JsonElement Locations,
    JsonElement Units,
    JsonElement Recipes,
    JsonElement RecipeLines);

/// <summary>What a Grocy import would bring in, or did, area by area.</summary>
public sealed record GrocyImportResult(int Chores, int Stock, int Batteries, int Recipes, int AlreadyThere, IReadOnlyList<string> Skipped);

/// <summary>
/// Brings in a Grocy household: chores as shared repeating chores, stock as the pantry, batteries
/// as things with a "Charge" upkeep, and recipes. Reads Grocy's API with an API key and changes
/// nothing there. Importing again adds nothing already brought in.
/// </summary>
public static partial class GrocyImport
{
    private const string Marker = "Imported from Grocy";
    private const string Never = "2999-12-31";

    /// <summary>The API paths read, all of them GETs.</summary>
    public static readonly string[] Paths =
    [
        "api/objects/chores", "api/chores", "api/objects/batteries", "api/batteries", "api/objects/products", "api/stock",
        "api/objects/locations", "api/objects/quantity_units", "api/objects/recipes", "api/objects/recipes_pos",
    ];

    /// <summary>Reads everything through <paramref name="get"/>, which returns the JSON at an API path.</summary>
    public static async Task<GrocySnapshot> LoadAsync(Func<string, Task<JsonElement>> get)
    {
        var read = new JsonElement[Paths.Length];
        for (var i = 0; i < Paths.Length; i++)
        {
            read[i] = await get(Paths[i]);
        }

        return new GrocySnapshot(read[0], read[1], read[2], read[3], read[4], read[5], read[6], read[7], read[8], read[9]);
    }

    /// <summary>A reader for Grocy's API at an address, with its API key (Grocy: user menu → Manage API keys).</summary>
    public static Func<string, Task<JsonElement>> Client(HttpClient http, Uri address, string apiKey) => async path =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, path));
        request.Headers.Add("GROCY-API-KEY", apiKey);
        using var response = await http.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new FormatException("Grocy didn't accept that API key.");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    };

    /// <param name="keepDueDates">
    /// Give chores Grocy's next due dates, so reminders start. Without, each chore keeps its repeat
    /// and is first due a repeat after it's next done, so nothing reminds until then.
    /// </param>
    public static GrocyImportResult Apply(GrocySnapshot grocy, TaskRepository tasks, PantryRepository pantry, InventoryRepository inventory,
        UpkeepRepository upkeep, MealRepository meals, Guid householdId, Guid ownerId, bool keepDueDates, DateOnly today, bool dryRun = false)
    {
        var skipped = new List<string>();
        var already = 0;

        // Chores go through the task import, which knows what it brought in before by source.
        var chores = Chores(grocy, keepDueDates, skipped);
        var choreResult = TaskImport.Apply(tasks, householdId, ownerId, chores, dryRun);
        already += choreResult.AlreadyThere;
        skipped.AddRange(choreResult.Problems);

        var locations = Names(grocy.Locations);
        var units = Names(grocy.Units);
        var products = ById(grocy.Products);

        // Stock: what's in, plus anything with a minimum amount, so running low works from day one.
        var stockAdded = 0;
        var have = pantry.Stock(householdId).Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var inStock = new Dictionary<string, (decimal Amount, DateOnly? BestBefore)>();
        foreach (var line in Items(grocy.Stock))
        {
            if (Text(line, "product_id") is { } id)
            {
                inStock[id] = (Number(line, "amount") ?? 0, Date(Text(line, "best_before_date")));
            }
        }

        foreach (var (id, product) in products)
        {
            if (Text(product, "active") == "0" || Text(product, "name") is not { } name)
            {
                continue;
            }

            var minimum = Number(product, "min_stock_amount") ?? 0;
            if (!inStock.TryGetValue(id, out var stock) && minimum <= 0)
            {
                continue;
            }

            if (!have.Add(name))
            {
                already++;
                continue;
            }

            try
            {
                var item = new StockItem
                {
                    HouseholdId = householdId,
                    Name = name,
                    Amount = stock.Amount,
                    Unit = Text(product, "qu_id_stock") is { } unit ? units.GetValueOrDefault(unit) : null,
                    Location = Text(product, "location_id") is { } place ? locations.GetValueOrDefault(place) : null,
                    BestBefore = stock.BestBefore,
                    LowAt = minimum > 0 ? minimum : null,
                };
                if (!dryRun)
                {
                    pantry.AddStock(item);
                }

                stockAdded++;
            }
            catch (ArgumentException exception)
            {
                skipped.Add($"Product {name}: {Short(exception)}");
            }
        }

        // Batteries: a thing each, with a "Charge" upkeep at Grocy's interval from its last charge.
        var batteriesAdded = 0;
        var marked = inventory.Items(householdId).Select(item => item.Notes).OfType<string>()
            .SelectMany(notes => notes.Split('\n')).Where(line => line.StartsWith(Marker, StringComparison.Ordinal)).ToHashSet();
        var lastCharged = Items(grocy.BatteriesDue).Where(row => Text(row, "battery_id") is not null)
            .ToDictionary(row => Text(row, "battery_id")!, row => Date(Text(row, "last_tracked_time")));
        foreach (var battery in Items(grocy.Batteries))
        {
            if (Text(battery, "active") == "0" || Text(battery, "name") is not { } name || Text(battery, "id") is not { } id)
            {
                continue;
            }

            var marker = $"{Marker} (battery {id})";
            if (!marked.Add(marker))
            {
                already++;
                continue;
            }

            var notes = string.Join("\n", new[] { Plain(Text(battery, "description")), Text(battery, "used_in") is { } usedIn ? $"Used in: {usedIn}" : null, marker }.OfType<string>());
            var item = new InventoryItem { HouseholdId = householdId, Name = name, Category = "Batteries", Notes = notes };
            if (!dryRun)
            {
                inventory.Add(item);
                if (Number(battery, "charge_interval_days") is > 0 and var days)
                {
                    upkeep.AddUpkeep(new Upkeep
                    {
                        HouseholdId = householdId,
                        ItemId = item.Id,
                        Name = "Charge",
                        EveryDays = (int)days,
                        LastDoneOn = lastCharged.GetValueOrDefault(id) ?? today,
                    });
                }
            }

            batteriesAdded++;
        }

        // Recipes: Grocy's own meal-plan helper recipes are left out.
        var recipesAdded = 0;
        var known = meals.Recipes(householdId).Select(recipe => recipe.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var lines = Items(grocy.RecipeLines).GroupBy(line => Text(line, "recipe_id") ?? "").ToDictionary(group => group.Key, group => group.ToList());
        foreach (var recipe in Items(grocy.Recipes))
        {
            if (Text(recipe, "type") is { } type && type != "normal" || Text(recipe, "name") is not { } name || Text(recipe, "id") is not { } id)
            {
                continue;
            }

            if (!known.Add(name))
            {
                already++;
                continue;
            }

            var ingredients = lines.GetValueOrDefault(id, []).Select(line =>
            {
                var product = Text(line, "product_id") is { } productId && products.TryGetValue(productId, out var found) ? Text(found, "name") : null;
                var amount = Number(line, "amount");
                var unit = Text(line, "qu_id") is { } unitId ? units.GetValueOrDefault(unitId) : null;
                return string.Join(" ", new[] { amount is { } a ? a.ToString("0.##", CultureInfo.InvariantCulture) : null, unit, product ?? Text(line, "note") }.OfType<string>());
            }).Where(text => text.Length > 0).ToList();

            try
            {
                var made = new Recipe
                {
                    HouseholdId = householdId,
                    Name = name,
                    Servings = Number(recipe, "base_servings") is > 0 and var servings ? (int)servings : null,
                    Ingredients = ingredients.Select(Ingredient.Parse).ToList(),
                    Instructions = Plain(Text(recipe, "description")),
                };
                if (!dryRun)
                {
                    meals.AddRecipe(made);
                }

                recipesAdded++;
            }
            catch (ArgumentException exception)
            {
                skipped.Add($"Recipe {name}: {Short(exception)}");
            }
        }

        return new GrocyImportResult(choreResult.Added, stockAdded, batteriesAdded, recipesAdded, already, skipped);
    }

    /// <summary>Grocy's chores as shared repeating chores, in the task import's own terms.</summary>
    public static List<ImportedTask> Chores(GrocySnapshot grocy, bool keepDueDates, List<string> skipped)
    {
        var due = Items(grocy.ChoresDue).Where(row => Text(row, "chore_id") is not null)
            .ToDictionary(row => Text(row, "chore_id")!, row => Date(Text(row, "next_estimated_execution_time")));
        var imported = new List<ImportedTask>();
        foreach (var chore in Items(grocy.Chores))
        {
            if (Text(chore, "active") == "0" || Text(chore, "name") is not { } name || Text(chore, "id") is not { } id)
            {
                continue;
            }

            var interval = Math.Max(1, (int)(Number(chore, "period_interval") ?? 1));
            var next = due.GetValueOrDefault(id);
            var description = Plain(Text(chore, "description"));
            ImportedTask Chore(string title, ImportedRepeat? repeat, DateOnly? on, string? schedule, string source) => new()
            {
                Title = title,
                Shared = true,
                Repeat = repeat,
                Due = keepDueDates && on is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                Notes = string.Join("\n", new[] { description, schedule is null ? null : $"In Grocy: {schedule}" }.OfType<string>()) is { Length: > 0 } notes ? notes : null,
                Labels = ["grocy"],
                Source = source,
            };

            switch (Text(chore, "period_type"))
            {
                case "daily":
                    imported.Add(Chore(name, new ImportedRepeat(interval, "days"), next, null, $"grocy:chore:{id}"));
                    break;
                case "hourly" when interval % 24 == 0:
                    // Grocy stores "every N days after it was last done" as hours.
                    imported.Add(Chore(name, new ImportedRepeat(interval / 24, "days"), next, null, $"grocy:chore:{id}"));
                    break;
                case "hourly":
                    imported.Add(Chore(name, new ImportedRepeat(1, "days"), next, $"every {interval} hours", $"grocy:chore:{id}"));
                    break;
                case "weekly":
                    var days = (Text(chore, "period_config") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(day => Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var weekday) ? weekday : (DayOfWeek?)null).OfType<DayOfWeek>().Distinct().ToList();
                    if (days.Count <= 1)
                    {
                        imported.Add(Chore(name, new ImportedRepeat(interval, "weeks"), next, days.Count == 1 ? $"weekly on {days[0]}" : null, $"grocy:chore:{id}"));
                    }
                    else
                    {
                        // One chore per weekday keeps each on its own day.
                        foreach (var day in days)
                        {
                            var on = next is { } from ? from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7) : (DateOnly?)null;
                            imported.Add(Chore($"{name} ({day})", new ImportedRepeat(interval, "weeks"), on, $"weekly on {string.Join(", ", days)}", $"grocy:chore:{id}:{day}"));
                        }
                    }

                    break;
                case "monthly":
                    imported.Add(Chore(name, new ImportedRepeat(interval, "months"), next, null, $"grocy:chore:{id}"));
                    break;
                case "yearly":
                    imported.Add(Chore(name, new ImportedRepeat(12 * interval, "months"), next, null, $"grocy:chore:{id}"));
                    break;
                case "adaptive":
                    imported.Add(Chore(name, null, next, "adaptive (Grocy worked out how often from when it was done)", $"grocy:chore:{id}"));
                    break;
                case "manually":
                    imported.Add(Chore(name, null, null, "done whenever needed", $"grocy:chore:{id}"));
                    break;
                default:
                    skipped.Add($"Chore {name}: Grocy's schedule \"{Text(chore, "period_type")}\" isn't one EtchaSketch knows");
                    break;
            }
        }

        return imported;
    }

    /// <summary>Grocy's descriptions are HTML from its editor; this keeps the text and its line breaks.</summary>
    public static string? Plain(string? html)
    {
        if (html is null)
        {
            return null;
        }

        var text = LineBreaks().Replace(html, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(" ", " ", StringComparison.Ordinal);
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim())).Trim('\n');
        text = ExtraBlankLines().Replace(text, "\n\n");
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"<\s*br\s*/?>|</\s*(p|div|li|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    private static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static Dictionary<string, JsonElement> ById(JsonElement array) =>
        Items(array).Where(item => Text(item, "id") is not null).ToDictionary(item => Text(item, "id")!);

    private static Dictionary<string, string> Names(JsonElement array) =>
        Items(array).Where(item => Text(item, "id") is not null && Text(item, "name") is not null)
            .ToDictionary(item => Text(item, "id")!, item => Text(item, "name")!);

    // Grocy has written numbers both as numbers and as strings over the years.
    private static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() is { Length: > 0 } text ? text : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static decimal? Number(JsonElement item, string property) =>
        decimal.TryParse(Text(item, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    // "2026-10-23 00:00:00" or "2026-10-23"; Grocy's "never" date counts as none.
    private static DateOnly? Date(string? text) =>
        text is { Length: >= 10 } && !text.StartsWith(Never, StringComparison.Ordinal)
        && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string Short(ArgumentException exception) => exception.Message.Split(" (Parameter")[0];
}
