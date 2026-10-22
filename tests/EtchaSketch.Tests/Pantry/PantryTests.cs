using System.Globalization;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Notifications;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Pantry;

public sealed class PantryTests : IDisposable
{
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-pantry-").FullName;
    private readonly SqliteConnection _db;
    private readonly PantryRepository _pantry;
    private readonly NotificationRepository _notifications;
    private readonly Household _home;

    public PantryTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        var households = new HouseholdRepository(_db);
        var created = households.Create("Home", "Robin");
        households.AddMember(created.Id, "Kai");
        _home = households.Get(created.Id)!;
        _pantry = new PantryRepository(_db);
        _notifications = new NotificationRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private StockItem Stock(string name, decimal amount, decimal? lowAt = null, DateOnly? bestBefore = null, string? location = null, string? unit = null) =>
        _pantry.AddStock(new StockItem { HouseholdId = _home.Id, Name = name, Amount = amount, LowAt = lowAt, BestBefore = bestBefore, Location = location, Unit = unit });

    [Fact]
    public void Stock_reads_back_exactly_and_sorts_by_place_then_name()
    {
        Stock("rice", 1.5m, unit: "kg", location: "Pantry");
        Stock("Milk", 2, location: "Fridge");
        Stock("Apples", 6, location: "Fridge");
        Stock("Batteries", 8);

        Assert.Equal(["Apples", "Milk", "rice", "Batteries"], _pantry.Stock(_home.Id).Select(item => item.Name));
        Assert.Equal(1.5m, _pantry.Stock(_home.Id).Single(item => item.Name == "rice").Amount);
        Assert.Throws<ArgumentException>(() => Stock(" ", 1));
        Assert.Throws<ArgumentException>(() => Stock("Negative", -1));
    }

    [Fact]
    public void Using_some_never_goes_below_nothing_and_low_stock_is_noticed()
    {
        var milk = Stock("Milk", 2, lowAt: 1);
        Assert.Empty(_pantry.Low(_home.Id));

        _pantry.Use(milk.Id, 1);
        Assert.Equal("Milk", Assert.Single(_pantry.Low(_home.Id)).Name);
        Assert.Equal(0, _pantry.Use(milk.Id, 5).Amount);
    }

    [Fact]
    public void What_runs_low_goes_on_the_list_once_and_ticking_it_puts_it_back_in_stock()
    {
        var milk = Stock("Milk", 0, lowAt: 1);
        Stock("Rice", 3, lowAt: 1);

        Assert.Equal(1, _pantry.AddLowToList(_home.Id));
        Assert.Equal(0, _pantry.AddLowToList(_home.Id));
        var line = Assert.Single(_pantry.ShoppingList(_home.Id));
        Assert.Equal((milk.Id, "Milk"), (line.StockItemId!.Value, line.Name));

        _pantry.Tick(line.Id, Wednesday);
        _pantry.Tick(line.Id, Wednesday);
        Assert.Equal(1, _pantry.GetStock(milk.Id)!.Amount);

        _pantry.Untick(line.Id);
        Assert.Equal(0, _pantry.GetStock(milk.Id)!.Amount);
    }

    [Fact]
    public void The_same_thing_isnt_added_twice_and_bought_lines_can_be_cleared()
    {
        var first = _pantry.AddToList(new ShoppingItem { HouseholdId = _home.Id, Name = "Eggs", Amount = 12 });
        var again = _pantry.AddToList(new ShoppingItem { HouseholdId = _home.Id, Name = "eggs" });
        _pantry.AddToList(new ShoppingItem { HouseholdId = _home.Id, Name = "Bread" });

        Assert.Equal(first.Id, again.Id);
        _pantry.Tick(first.Id, Wednesday);
        Assert.Equal(["Bread", "Eggs"], _pantry.ShoppingList(_home.Id).Select(line => line.Name));
        Assert.Equal(1, _pantry.ClearBought(_home.Id));
        Assert.Equal(["Bread"], _pantry.ShoppingList(_home.Id).Select(line => line.Name));
    }

    [Fact]
    public void Something_about_to_go_off_warns_each_person_once()
    {
        Stock("Milk", 1, bestBefore: Today.AddDays(3), location: "Fridge");
        Stock("Yoghurt", 1, bestBefore: Today.AddDays(-1));
        Stock("Rice", 1, bestBefore: Today.AddDays(30));
        Stock("Empty", 0, bestBefore: Today);
        var alerts = new PantryAlerts(_pantry, _notifications);

        Assert.Equal(4, alerts.Check(_home, Wednesday));
        Assert.Equal(0, alerts.Check(_home, Wednesday.AddHours(2)));

        var feed = _notifications.Feed(_home.Members[0].Id, Wednesday).Shown;
        Assert.Contains(feed, n => n is { Title: "Milk goes off in 3 days", Body: "In the fridge", Kind: NotificationKind.Expiring });
        Assert.Contains(feed, n => n.Title == "Yoghurt is past its best-before date");
        Assert.DoesNotContain(feed, n => n.Title.StartsWith("Rice", StringComparison.Ordinal) || n.Title.StartsWith("Empty", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("200 g rice", "rice", "200", "g")]
    [InlineData("2 eggs", "eggs", "2", null)]
    [InlineData("1,5 l milk", "milk", "1.5", "l")]
    [InlineData("salt", "salt", null, null)]
    [InlineData("3 garlic cloves", "garlic cloves", "3", null)]
    public void Ingredient_lines_are_read_into_amount_unit_and_name(string line, string name, string? amount, string? unit)
    {
        var ingredient = Ingredient.Parse(line);

        Assert.Equal((name, amount is null ? (decimal?)null : decimal.Parse(amount, CultureInfo.InvariantCulture), unit),
            (ingredient.Name, ingredient.Amount, ingredient.Unit));
    }

    private Recipe Curry(MealRepository meals) => meals.AddRecipe(new Recipe
    {
        HouseholdId = _home.Id, Name = "Curry", Servings = 4,
        Ingredients = [Ingredient.Parse("300 g rice"), Ingredient.Parse("1 can coconut milk"), Ingredient.Parse("2 onions"), Ingredient.Parse("salt")],
    });

    [Fact]
    public void Planning_a_recipe_puts_exactly_what_is_missing_on_the_list_once()
    {
        var meals = new MealRepository(_db, _pantry);
        Stock("Rice", 1000, unit: "g");
        Stock("Onions", 1);
        Stock("Salt", 1, unit: "jar");
        var curry = Curry(meals);

        var (meal, added) = meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Today, RecipeId = curry.Id, Title = "" });
        var (_, again) = meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Today.AddDays(1), RecipeId = curry.Id, Title = "" });

        Assert.Equal("Curry", meal.Title);
        Assert.Equal([("coconut milk", 1m, "can"), ("Onions", 1m, null)], added.Select(line => (line.Name, line.Amount!.Value, line.Unit)));
        Assert.Empty(again);
        Assert.Equal(2, meals.Meals(_home.Id, Today, Today.AddDays(6)).Count);
    }

    [Fact]
    public void Cooking_takes_the_ingredients_out_of_stock_once()
    {
        var meals = new MealRepository(_db, _pantry);
        var rice = Stock("Rice", 1000, unit: "g");
        var onions = Stock("Onions", 5);
        var salt = Stock("Salt", 1, unit: "jar");
        var curry = Curry(meals);
        var (meal, _) = meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Today, RecipeId = curry.Id, Title = "" });

        meals.Cook(meal.Id, Wednesday);
        meals.Cook(meal.Id, Wednesday);

        Assert.Equal(700, _pantry.GetStock(rice.Id)!.Amount);
        Assert.Equal(3, _pantry.GetStock(onions.Id)!.Amount);
        Assert.Equal(1, _pantry.GetStock(salt.Id)!.Amount);
        Assert.NotNull(meals.Meals(_home.Id, Today, Today).Single().CookedAt);
    }

    [Fact]
    public void A_meal_without_a_recipe_needs_a_name_and_deleting_a_recipe_keeps_planned_meals()
    {
        var meals = new MealRepository(_db, _pantry);
        Assert.Throws<ArgumentException>(() => meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Today, Title = " " }));
        var curry = Curry(meals);
        meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Today, Meal = Meal.Lunch, Title = "Takeaway" });
        meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Today, RecipeId = curry.Id, Title = "" });

        meals.DeleteRecipe(curry.Id);

        Assert.Equal([("Takeaway", Meal.Lunch), ("Curry", Meal.Dinner)], meals.Meals(_home.Id, Today, Today).Select(m => (m.Title, m.Meal)));
        Assert.Empty(meals.Recipes(_home.Id));
    }
}
