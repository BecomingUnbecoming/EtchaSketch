using System.Globalization;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Notifications;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Pantry;

public sealed class UpkeepTests : IDisposable
{
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-upkeep-").FullName;
    private readonly SqliteConnection _db;
    private readonly InventoryRepository _inventory;
    private readonly UpkeepRepository _upkeep;
    private readonly NotificationRepository _notifications;
    private readonly Household _home;

    public UpkeepTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        var households = new HouseholdRepository(_db);
        var created = households.Create("Home", "Robin");
        households.AddMember(created.Id, "Kai");
        _home = households.Get(created.Id)!;
        _inventory = new InventoryRepository(_db);
        _upkeep = new UpkeepRepository(_db, _inventory);
        _notifications = new NotificationRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private InventoryItem Car() => _inventory.Add(new InventoryItem { HouseholdId = _home.Id, Name = "Corolla", DistanceUnit = "km" });

    private ItemLogEntry Fuel(InventoryItem car, int distance, DateOnly? on = null, decimal? cost = null) =>
        _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = car.Id, Kind = LogKind.Fuel, On = on ?? Today, Distance = distance, Amount = 40, Cost = cost });

    [Fact]
    public void A_service_due_by_distance_reminds_after_a_fuel_entry_passes_the_distance()
    {
        var car = Car();
        Fuel(car, 30_000, Today.AddDays(-30));
        _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = "Oil change", EveryDistance = 10_000, LastDoneOn = Today.AddDays(-30), LastDoneDistance = 30_000 });

        Fuel(car, 39_900, Today.AddDays(-1));
        Assert.Equal(0, _upkeep.CheckDue(_home, _notifications, Wednesday));

        Fuel(car, 40_250);
        Assert.Equal(2, _upkeep.CheckDue(_home, _notifications, Wednesday));
        Assert.Equal(0, _upkeep.CheckDue(_home, _notifications, Wednesday.AddHours(2)));

        var shown = Assert.Single(_notifications.Feed(_home.Members[0].Id, Wednesday).Shown);
        Assert.Equal(("Corolla: Oil change is due", "Every 10,000 km · now at 40,250 km"), (shown.Title, shown.Body));
    }

    [Fact]
    public void Doing_the_upkeep_moves_it_on_and_it_warns_again_next_time()
    {
        var car = Car();
        var oil = _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = "Oil change", EveryDistance = 10_000, EveryDays = 365, LastDoneOn = Today.AddDays(-400), LastDoneDistance = 0 });
        Assert.Equal(2, _upkeep.CheckDue(_home, _notifications, Wednesday));

        _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = car.Id, Kind = LogKind.Service, On = Today, Distance = 8_000, Cost = 89.90m, UpkeepId = oil.Id });

        var done = _upkeep.GetUpkeep(oil.Id)!;
        Assert.Equal((Today, 8_000), (done.LastDoneOn, done.LastDoneDistance));
        Assert.Equal((Today.AddDays(365), 18_000), (done.NextOn, done.NextAtDistance));
        Assert.False(done.IsDue(Today, 8_000));
        Assert.Equal(0, _upkeep.CheckDue(_home, _notifications, Wednesday));

        Fuel(car, 18_100, Today.AddDays(100));
        Assert.Equal(2, _upkeep.CheckDue(_home, _notifications, Wednesday.AddDays(100)));
    }

    [Fact]
    public void An_older_entry_logged_late_does_not_undo_a_newer_one()
    {
        var car = Car();
        var oil = _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = "Oil change", EveryDistance = 10_000, LastDoneOn = Today, LastDoneDistance = 50_000 });

        _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = car.Id, Kind = LogKind.Service, On = Today.AddDays(-200), Distance = 41_000, UpkeepId = oil.Id });

        Assert.Equal((Today, 50_000), (_upkeep.GetUpkeep(oil.Id)!.LastDoneOn, _upkeep.GetUpkeep(oil.Id)!.LastDoneDistance));
    }

    [Fact]
    public void A_battery_counts_its_charges_and_is_due_by_date()
    {
        var battery = _inventory.Add(new InventoryItem { HouseholdId = _home.Id, Name = "Drill battery 2" });
        var charge = _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = battery.Id, Name = "Charge", EveryDays = 90, LastDoneOn = Today.AddDays(-89) });
        Assert.Equal(0, _upkeep.CheckDue(_home, _notifications, Wednesday));
        Assert.Equal(2, _upkeep.CheckDue(_home, _notifications, Wednesday.AddDays(1)));

        for (var i = 0; i < 3; i++)
        {
            _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = battery.Id, Kind = LogKind.Charge, On = Today.AddDays(i), UpkeepId = charge.Id });
        }

        Assert.Equal(3, _upkeep.TimesDone(charge.Id));
        Assert.Equal(Today.AddDays(2 + 90), _upkeep.GetUpkeep(charge.Id)!.NextOn);
    }

    [Fact]
    public void The_log_reads_back_newest_first_with_costs_added_up()
    {
        var car = Car();
        Fuel(car, 1_000, Today.AddDays(-10), cost: 61.20m);
        Fuel(car, 1_600, Today, cost: 58.05m);
        _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = car.Id, Kind = LogKind.Repair, On = Today.AddDays(-5), Cost = 240m, Note = "New wiper motor" });

        Assert.Equal([1_600, null, 1_000], _upkeep.LogFor(car.Id).Select(entry => entry.Distance));
        Assert.Equal(359.25m, _upkeep.TotalCost(car.Id));
        Assert.Equal(1_600, _upkeep.DistanceNow(car.Id));
        Assert.Equal("New wiper motor", _upkeep.LogFor(car.Id)[1].Note);
    }

    [Fact]
    public void Upkeep_and_entries_are_checked()
    {
        var car = Car();
        var kettle = _inventory.Add(new InventoryItem { HouseholdId = _home.Id, Name = "Kettle" });

        Assert.Throws<ArgumentException>(() => _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = "Tyres", LastDoneOn = Today }));
        Assert.Throws<ArgumentException>(() => _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = " ", EveryDays = 30, LastDoneOn = Today }));
        Assert.Throws<ArgumentException>(() => _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = "Tyres", EveryDays = 0, LastDoneOn = Today }));
        Assert.Throws<ArgumentException>(() => _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = kettle.Id, Name = "Descale", EveryDistance = 100, LastDoneOn = Today }));
        Assert.Throws<ArgumentException>(() => _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = kettle.Id, Kind = LogKind.Note, On = Today, Distance = 5 }));
        Assert.Throws<ArgumentException>(() => _upkeep.Log(new ItemLogEntry { HouseholdId = _home.Id, ItemId = car.Id, Kind = LogKind.Fuel, On = Today, Cost = -1 }));
        Assert.Throws<ArgumentException>(() => _inventory.Add(new InventoryItem { HouseholdId = _home.Id, Name = "Boat", DistanceUnit = "nautical miles" }));
        Assert.Equal("mi", _inventory.Add(new InventoryItem { HouseholdId = _home.Id, Name = "Truck", DistanceUnit = " MI " }).DistanceUnit);
    }

    [Fact]
    public void Deleting_an_item_takes_its_upkeep_and_log_with_it()
    {
        var car = Car();
        var oil = _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = car.Id, Name = "Oil change", EveryDays = 365, LastDoneOn = Today });
        Fuel(car, 100);

        _inventory.Delete(car.Id);

        Assert.Null(_upkeep.GetUpkeep(oil.Id));
        Assert.Empty(_upkeep.LogFor(car.Id));
    }
}
