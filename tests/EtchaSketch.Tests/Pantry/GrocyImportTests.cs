using System.Globalization;
using System.Text.Json;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Import;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Pantry;

public sealed class GrocyImportTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 21); // a Wednesday

    // Shaped like Grocy 4's API answers. Some numbers are strings, as older Grocy versions wrote them.
    private static readonly Dictionary<string, string> Api = new()
    {
        ["api/objects/chores"] = """
            [
              {"id":1,"name":"Water plants","description":"<p>Kitchen &amp; hall</p><p>Not the cactus</p>","period_type":"daily","period_interval":3,"active":1},
              {"id":2,"name":"Change sheets","description":null,"period_type":"hourly","period_interval":168,"active":1},
              {"id":3,"name":"Bins out","description":"","period_type":"weekly","period_config":"monday,thursday","period_interval":1,"active":1},
              {"id":4,"name":"Descale kettle","period_type":"monthly","period_days":1,"period_interval":"2","active":"1"},
              {"id":5,"name":"Gutters","period_type":"yearly","period_interval":1,"active":1},
              {"id":6,"name":"Defrost freezer","period_type":"manually","period_interval":1,"active":1},
              {"id":7,"name":"Old chore","period_type":"daily","period_interval":1,"active":0},
              {"id":8,"name":"Feed sourdough","period_type":"hourly","period_interval":12,"active":1},
              {"id":9,"name":"Vacuum","period_type":"weekly","period_config":"saturday","period_interval":1,"active":1}
            ]
            """,
        ["api/chores"] = """
            [
              {"chore_id":1,"next_estimated_execution_time":"2026-10-22 00:00:00"},
              {"chore_id":2,"next_estimated_execution_time":"2026-10-25 00:00:00"},
              {"chore_id":3,"next_estimated_execution_time":"2026-10-22 00:00:00"},
              {"chore_id":4,"next_estimated_execution_time":"2026-11-01 00:00:00"},
              {"chore_id":5,"next_estimated_execution_time":"2027-04-01 00:00:00"},
              {"chore_id":6,"next_estimated_execution_time":"2999-12-31 23:59:59"},
              {"chore_id":8,"next_estimated_execution_time":"2026-10-21 20:00:00"},
              {"chore_id":9,"next_estimated_execution_time":"2026-10-24 00:00:00"}
            ]
            """,
        ["api/objects/batteries"] = """[{"id":1,"name":"Drill battery 2","description":"18V 4Ah","used_in":"Drill","charge_interval_days":90,"active":1}]""",
        ["api/batteries"] = """[{"battery_id":"1","last_tracked_time":"2026-09-01 10:00:00"}]""",
        ["api/objects/products"] = """
            [
              {"id":1,"name":"Milk","location_id":1,"qu_id_stock":1,"min_stock_amount":2,"active":1},
              {"id":2,"name":"Rice","location_id":2,"qu_id_stock":2,"min_stock_amount":0,"active":1},
              {"id":3,"name":"Coffee","location_id":2,"qu_id_stock":3,"min_stock_amount":"1","active":1},
              {"id":4,"name":"Saffron","location_id":2,"qu_id_stock":2,"min_stock_amount":0,"active":1}
            ]
            """,
        ["api/stock"] = """
            [
              {"product_id":1,"amount":"1","best_before_date":"2026-10-24"},
              {"product_id":2,"amount":1500,"best_before_date":"2999-12-31"}
            ]
            """,
        ["api/objects/locations"] = """[{"id":1,"name":"Fridge"},{"id":2,"name":"Pantry"}]""",
        ["api/objects/quantity_units"] = """[{"id":1,"name":"Bottle"},{"id":2,"name":"g"},{"id":3,"name":"Pack"}]""",
        ["api/objects/recipes"] = """
            [
              {"id":1,"name":"Rice pudding","description":"<ol><li>Simmer</li><li>Stir</li></ol>","base_servings":4,"type":"normal"},
              {"id":-1,"name":"2026-10-21","type":"mealplan-day"}
            ]
            """,
        ["api/objects/recipes_pos"] = """
            [
              {"id":1,"recipe_id":1,"product_id":2,"amount":200,"qu_id":2},
              {"id":2,"recipe_id":1,"product_id":1,"amount":"1","qu_id":1}
            ]
            """,
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-grocy-").FullName;
    private readonly SqliteConnection _db;
    private readonly TaskRepository _tasks;
    private readonly PantryRepository _pantry;
    private readonly InventoryRepository _inventory;
    private readonly UpkeepRepository _upkeep;
    private readonly MealRepository _meals;
    private readonly Core.Profiles.Household _home;

    public GrocyImportTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        _home = new HouseholdRepository(_db).Create("Home", "Robin");
        _tasks = new TaskRepository(_db);
        _pantry = new PantryRepository(_db);
        _inventory = new InventoryRepository(_db);
        _upkeep = new UpkeepRepository(_db, _inventory);
        _meals = new MealRepository(_db, _pantry);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static Task<GrocySnapshot> Load() => GrocyImport.LoadAsync(path => Task.FromResult(JsonDocument.Parse(Api[path]).RootElement.Clone()));

    private GrocyImportResult Apply(GrocySnapshot grocy, bool keepDueDates, bool dryRun = false) =>
        GrocyImport.Apply(grocy, _tasks, _pantry, _inventory, _upkeep, _meals, _home.Id, _home.Members[0].Id, keepDueDates, Today, dryRun);

    private TaskItem Chore(string title) => _tasks.VisibleTo(_home.Members[0].Id).Single(task => task.Title == title);

    [Fact]
    public async Task Every_kind_of_grocy_schedule_becomes_a_shared_repeating_chore()
    {
        var result = Apply(await Load(), keepDueDates: true);

        Assert.Equal(9, result.Chores);
        Assert.All(_tasks.VisibleTo(_home.Members[0].Id), task => Assert.Equal(Core.Privacy.PrivacyTier.Household, task.Tier));

        Assert.Equal((new Recurrence(RecurrenceFrequency.Daily, 3), new DateOnly(2026, 10, 22)), (Chore("Water plants").Recurrence, Chore("Water plants").Due));
        Assert.Equal(new Recurrence(RecurrenceFrequency.Daily, 7), Chore("Change sheets").Recurrence);
        Assert.Equal((new Recurrence(RecurrenceFrequency.Weekly), new DateOnly(2026, 10, 26)), (Chore("Bins out (Monday)").Recurrence, Chore("Bins out (Monday)").Due));
        Assert.Equal(new DateOnly(2026, 10, 22), Chore("Bins out (Thursday)").Due);
        Assert.Equal(new Recurrence(RecurrenceFrequency.Monthly, 2), Chore("Descale kettle").Recurrence);
        Assert.Equal(new Recurrence(RecurrenceFrequency.Monthly, 12), Chore("Gutters").Recurrence);
        Assert.Equal((null, null), (Chore("Defrost freezer").Recurrence, Chore("Defrost freezer").Due));
        Assert.Equal(new Recurrence(RecurrenceFrequency.Daily, 1), Chore("Feed sourdough").Recurrence);
        Assert.Equal(new DateOnly(2026, 10, 24), Chore("Vacuum").Due);
        Assert.DoesNotContain(_tasks.VisibleTo(_home.Members[0].Id), task => task.Title == "Old chore");
        Assert.Equal(["grocy"], Chore("Vacuum").Labels);
    }

    [Fact]
    public async Task Descriptions_lose_their_html_and_odd_schedules_are_noted()
    {
        Apply(await Load(), keepDueDates: true);
        var notes = _tasks.VisibleTo(_home.Members[0].Id).ToDictionary(task => task.Title, task => task.Notes);

        Assert.StartsWith("Kitchen & hall\nNot the cactus\n", notes["Water plants"], StringComparison.Ordinal);
        Assert.Contains("In Grocy: every 12 hours", notes["Feed sourdough"], StringComparison.Ordinal);
        Assert.Contains("In Grocy: weekly on Monday, Thursday", notes["Bins out (Monday)"], StringComparison.Ordinal);
        Assert.Contains("In Grocy: done whenever needed", notes["Defrost freezer"], StringComparison.Ordinal);
        Assert.Equal("Simmer\nStir", GrocyImport.Plain("<ol><li>Simmer</li><li>Stir</li></ol>"));
        Assert.Null(GrocyImport.Plain("<p>&nbsp;</p>"));
    }

    [Fact]
    public async Task Without_due_dates_chores_keep_their_repeat_and_stay_quiet_until_first_done()
    {
        Apply(await Load(), keepDueDates: false);

        Assert.All(_tasks.VisibleTo(_home.Members[0].Id), task => Assert.Null(task.Due));
        var plants = Chore("Water plants");
        _tasks.Complete(plants.Id, _home.Members[0].Id, new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 10, 24), _tasks.Get(plants.Id)!.Due);
    }

    [Fact]
    public async Task Stock_batteries_and_recipes_come_in_too()
    {
        var result = Apply(await Load(), keepDueDates: false);

        Assert.Equal((3, 1, 1), (result.Stock, result.Batteries, result.Recipes));
        var stock = _pantry.Stock(_home.Id).ToDictionary(item => item.Name);
        Assert.Equal((1m, "Bottle", "Fridge", new DateOnly(2026, 10, 24), 2m), (stock["Milk"].Amount, stock["Milk"].Unit, stock["Milk"].Location, stock["Milk"].BestBefore, stock["Milk"].LowAt));
        Assert.Equal((1500m, (DateOnly?)null, (decimal?)null), (stock["Rice"].Amount, stock["Rice"].BestBefore, stock["Rice"].LowAt));
        Assert.Equal((0m, 1m), (stock["Coffee"].Amount, stock["Coffee"].LowAt));
        Assert.False(stock.ContainsKey("Saffron"));

        var battery = Assert.Single(_inventory.Items(_home.Id));
        Assert.Equal(("Drill battery 2", "Batteries", "18V 4Ah\nUsed in: Drill\nImported from Grocy (battery 1)"), (battery.Name, battery.Category, battery.Notes));
        var charge = Assert.Single(_upkeep.UpkeepFor(battery.Id));
        Assert.Equal(("Charge", 90, new DateOnly(2026, 9, 1)), (charge.Name, charge.EveryDays, charge.LastDoneOn));

        var pudding = Assert.Single(_meals.Recipes(_home.Id));
        Assert.Equal(("Rice pudding", 4, "Simmer\nStir"), (pudding.Name, pudding.Servings, pudding.Instructions));
        Assert.Equal(["200 g Rice", "1 Bottle Milk"], pudding.Ingredients.Select(ingredient => ingredient.ToString()));
    }

    [Fact]
    public async Task A_check_changes_nothing_and_a_second_import_adds_nothing()
    {
        var grocy = await Load();
        var preview = Apply(grocy, keepDueDates: false, dryRun: true);
        Assert.Equal((9, 3, 1, 1), (preview.Chores, preview.Stock, preview.Batteries, preview.Recipes));
        Assert.Empty(_tasks.VisibleTo(_home.Members[0].Id));
        Assert.Empty(_pantry.Stock(_home.Id));
        Assert.Empty(_inventory.Items(_home.Id));

        Apply(grocy, keepDueDates: false);
        var again = Apply(grocy, keepDueDates: false);

        Assert.Equal((0, 0, 0, 0, 14), (again.Chores, again.Stock, again.Batteries, again.Recipes, again.AlreadyThere));
    }

    [Fact]
    public async Task The_client_sends_the_api_key_and_explains_a_wrong_one()
    {
        var handler = new Answer();
        var get = GrocyImport.Client(new HttpClient(handler), new Uri("https://grocy.example/"), "secret-key");

        var chores = await get("api/objects/chores");
        Assert.Equal(JsonValueKind.Array, chores.ValueKind);
        Assert.Equal(("https://grocy.example/api/objects/chores", "secret-key", "GET"), (handler.Asked!.RequestUri!.ToString(), handler.Key, handler.Asked.Method.Method));

        handler.Status = System.Net.HttpStatusCode.Unauthorized;
        var problem = await Assert.ThrowsAsync<FormatException>(() => get("api/chores"));
        Assert.Equal("Grocy didn't accept that API key.", problem.Message);
    }

    private sealed class Answer : HttpMessageHandler
    {
        public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;

        public HttpRequestMessage? Asked { get; private set; }

        public string? Key { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request;
            Key = request.Headers.GetValues("GROCY-API-KEY").Single();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("[]") });
        }
    }
}
