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
}
