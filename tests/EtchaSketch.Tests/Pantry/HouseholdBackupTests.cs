using System.Globalization;
using System.Text.Json.Nodes;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Data.Import;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Pantry;

public sealed class HouseholdBackupTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-backup-").FullName;
    private readonly List<SqliteConnection> _open = [];

    public void Dispose()
    {
        foreach (var db in _open)
        {
            db.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    private (SqliteConnection Db, Guid Home) Install(string name)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var db = EncryptedDatabase.Open(Path.Combine(_directory, name + ".db"), new InMemoryKeyStore());
        Schema.Apply(db);
        _open.Add(db);
        return (db, new HouseholdRepository(db).Create("Home", "Robin").Id);
    }

    private static Dictionary<string, List<string>> Everything(SqliteConnection db)
    {
        var all = new Dictionary<string, List<string>>();
        foreach (var (table, _) in HouseholdBackup.Tables)
        {
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY id";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                    .Where(i => reader.GetName(i) != "household_id")
                    .Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))}")));
            }

            all[table] = rows;
        }

        return all;
    }

    private static void FillIn(SqliteConnection db, Guid home)
    {
        var pantry = new PantryRepository(db);
        var meals = new MealRepository(db, pantry);
        var inventory = new InventoryRepository(db);
        var upkeep = new UpkeepRepository(db, inventory);

        var rice = pantry.AddStock(new StockItem { HouseholdId = home, Name = "Rice", Amount = 0.75m, Unit = "kg", Location = "Pantry", LowAt = 1, BestBefore = Today.AddDays(90) });
        pantry.AddToList(new ShoppingItem { HouseholdId = home, Name = "Rice", StockItemId = rice.Id, Amount = 1.5m });
        pantry.AddToList(new ShoppingItem { HouseholdId = home, Name = "Bread" });
        var pudding = meals.AddRecipe(new Recipe { HouseholdId = home, Name = "Rice pudding", Servings = 4, Instructions = "Simmer", Ingredients = [Ingredient.Parse("200 g rice")] });
        meals.Plan(new PlannedMeal { HouseholdId = home, Day = Today, Meal = Meal.Dinner, RecipeId = pudding.Id, Title = "Rice pudding" });
        var car = inventory.Add(new InventoryItem { HouseholdId = home, Name = "Corolla", DistanceUnit = "km", Price = 12_500.50m, WarrantyEnds = Today.AddYears(1) });
        var oil = upkeep.AddUpkeep(new Upkeep { HouseholdId = home, ItemId = car.Id, Name = "Oil change", EveryDistance = 10_000, LastDoneOn = Today, LastDoneDistance = 30_000 });
        upkeep.Log(new ItemLogEntry { HouseholdId = home, ItemId = car.Id, Kind = LogKind.Service, On = Today, Distance = 30_000, Cost = 89.90m, UpkeepId = oil.Id, Note = "5W-30" });
    }

    [Fact]
    public void Everything_comes_back_identical_on_a_fresh_install()
    {
        var (old, oldHome) = Install("old");
        FillIn(old, oldHome);

        var backup = JsonNode.Parse(HouseholdBackup.Export(old, oldHome).ToJsonString())!.AsObject();
        var (fresh, freshHome) = Install("fresh");
        var restored = HouseholdBackup.Restore(fresh, backup, freshHome);

        Assert.Equal(8, restored.Total);
        Assert.Equal(Everything(old), Everything(fresh));
        Assert.Equal("Rice pudding", Assert.Single(new MealRepository(fresh, new PantryRepository(fresh)).Recipes(freshHome)).Name);
        Assert.Equal(30_000, new UpkeepRepository(fresh, new InventoryRepository(fresh)).DistanceNow(new InventoryRepository(fresh).Items(freshHome).Single().Id));
    }

    [Fact]
    public void Restoring_twice_adds_nothing_and_a_check_changes_nothing()
    {
        var (old, oldHome) = Install("old");
        FillIn(old, oldHome);
        var backup = HouseholdBackup.Export(old, oldHome);
        var (fresh, freshHome) = Install("fresh");

        Assert.Equal(8, HouseholdBackup.Restore(fresh, backup, freshHome, dryRun: true).Total);
        Assert.All(Everything(fresh).Values, Assert.Empty);

        HouseholdBackup.Restore(fresh, backup, freshHome);
        var again = HouseholdBackup.Restore(fresh, backup, freshHome);

        Assert.Equal((0, 8), (again.Total, again.AlreadyThere));
    }

    [Fact]
    public void A_backup_from_a_newer_version_still_restores_and_nothing_private_goes_in()
    {
        var (fresh, home) = Install("fresh");
        var backup = JsonNode.Parse("""
            {"stock_items":[
              {"id":"6d6b8a4e-1d55-4c0e-9c52-1a3c4f5e6a01","household_id":"someone-else","name":"Oats","amount":"1","tier":-1,"created_at":"2026-10-21T09:00:00Z","colour":"beige"},
              {"id":"6d6b8a4e-1d55-4c0e-9c52-1a3c4f5e6a02","household_id":"someone-else","name":"Secret stash","amount":"1","tier":0,"created_at":"2026-10-21T09:00:00Z"}
            ],
             "item_log":[{"id":"6d6b8a4e-1d55-4c0e-9c52-1a3c4f5e6a03","item_id":"6d6b8a4e-1d55-4c0e-9c52-1a3c4f5e6a99","kind":"Fuel","on_date":"2026-10-21","tier":-1,"created_at":"2026-10-21T09:00:00Z"}],
             "some_future_table":[{"id":"x"}]}
            """)!.AsObject();

        var restored = HouseholdBackup.Restore(fresh, backup, home);

        Assert.Equal(1, restored.Total);
        var oats = Assert.Single(new PantryRepository(fresh).Stock(home));
        Assert.Equal(("Oats", home), (oats.Name, oats.HouseholdId));
    }

    [Fact]
    public void A_file_without_a_household_part_reads_as_none()
    {
        Assert.Null(HouseholdBackup.Read("""{"format":"etchasketch-tasks","version":1,"tasks":[]}"""));
        Assert.Null(HouseholdBackup.Read("not json"));
        Assert.NotNull(HouseholdBackup.Read("""{"format":"etchasketch-tasks","version":1,"tasks":[],"household":{}}"""));
    }
}
