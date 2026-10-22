using System.Globalization;
using EtchaSketch.Core.Calendar;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Calendar;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Calendar;

public sealed class CalendarTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly DateOnly Sunday = Monday.AddDays(6);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-calendar-").FullName;
    private readonly SqliteConnection _db;
    private readonly TaskRepository _tasks;
    private readonly EventRepository _events;
    private readonly InventoryRepository _inventory;
    private readonly UpkeepRepository _upkeep;
    private readonly MealRepository _meals;
    private readonly CalendarFeed _feed;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;

    public CalendarTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        var households = new HouseholdRepository(_db);
        var created = households.Create("Home", "Robin");
        households.AddMember(created.Id, "Kai");
        _home = households.Get(created.Id)!;
        (_robin, _kai) = (_home.Members[0], _home.Members[1]);
        _tasks = new TaskRepository(_db);
        _events = new EventRepository(_db);
        _inventory = new InventoryRepository(_db);
        _upkeep = new UpkeepRepository(_db, _inventory);
        var pantry = new PantryRepository(_db);
        _meals = new MealRepository(_db, pantry);
        _feed = new CalendarFeed(_tasks, _events, _inventory, _upkeep, _meals);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private CalendarEvent Event(string title, DateOnly on, TimeOnly? start = null, TimeOnly? end = null, Profile? owner = null,
        PrivacyTier tier = PrivacyTier.Private, EventKind kind = EventKind.Event, DateOnly? endDate = null, string? notes = null) =>
        _events.Add(new CalendarEvent
        {
            HouseholdId = _home.Id, OwnerId = (owner ?? _robin).Id, Title = title, StartDate = on, StartTime = start, EndTime = end, EndDate = endDate,
            Tier = tier, Kind = kind, Notes = notes,
        });

    private List<string> Week(Profile who) =>
        _feed.Between(_home, who.Id, Monday, Sunday).Select(entry => $"{entry.Day:ddd} {entry.Start:HH\\:mm} {entry.Kind} {entry.Title}".Replace("  ", " ", StringComparison.Ordinal)).ToList();

    [Fact]
    public void A_task_with_a_time_a_chore_and_a_time_block_all_show_on_the_same_week()
    {
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Vet", Due = Monday.AddDays(2), DueTime = new TimeOnly(14, 5) });
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Bins out", Due = Monday, Tier = PrivacyTier.Household,
            Recurrence = new Recurrence(RecurrenceFrequency.Daily, 3),
        });
        Event("Deep work", Monday.AddDays(1), new TimeOnly(9, 0), new TimeOnly(11, 0), kind: EventKind.TimeBlock);

        Assert.Equal(
        [
            "Mon Chore Bins out",
            "Tue 09:00 TimeBlock Deep work",
            "Wed 14:05 Task Vet",
            "Thu Chore Bins out",
            "Sun Chore Bins out",
        ], Week(_robin));
    }

    [Fact]
    public void Someone_else_sees_shared_events_and_chores_but_nothing_private_and_no_notes()
    {
        Event("Dinner at Mum's", Monday, new TimeOnly(18, 30), tier: PrivacyTier.Household, notes: "Bring wine");
        Event("Therapy", Monday.AddDays(1), new TimeOnly(10, 0), notes: "Talk about work");
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Secret gift", Due = Monday.AddDays(3) });

        Assert.Equal(["Mon 18:30 Event Dinner at Mum's"], Week(_kai));
        Assert.Null(_events.VisibleTo(_kai.Id, Monday, Sunday).Single().Notes);
        Assert.Equal("Bring wine", _events.VisibleTo(_robin.Id, Monday, Sunday).Single(found => found.Title == "Dinner at Mum's").Notes);
        Assert.Equal(3, Week(_robin).Count);
    }

    [Fact]
    public void A_multi_day_event_shows_each_day_with_its_times_on_the_first_and_last()
    {
        Event("Camping", Monday.AddDays(4), new TimeOnly(16, 0), new TimeOnly(12, 0), endDate: Monday.AddDays(6));
        Event("Holiday", Monday.AddDays(-2), endDate: Monday.AddDays(1));

        var entries = _feed.Between(_home, _robin.Id, Monday, Sunday);

        Assert.Equal([Monday, Monday.AddDays(1)], entries.Where(entry => entry.Title == "Holiday").Select(entry => entry.Day));
        var camping = entries.Where(entry => entry.Title == "Camping").ToList();
        Assert.Equal([(new TimeOnly(16, 0), (TimeOnly?)null), (null, null), (null, new TimeOnly(12, 0))], camping.Select(entry => (entry.Start, entry.End)));
    }

    [Fact]
    public void Upkeep_warranties_and_meals_are_on_the_calendar_too()
    {
        var battery = _inventory.Add(new InventoryItem { HouseholdId = _home.Id, Name = "Drill battery", WarrantyEnds = Monday.AddDays(5) });
        _upkeep.AddUpkeep(new Upkeep { HouseholdId = _home.Id, ItemId = battery.Id, Name = "Charge", EveryDays = 90, LastDoneOn = Monday.AddDays(-87) });
        _meals.Plan(new PlannedMeal { HouseholdId = _home.Id, Day = Monday.AddDays(4), Meal = Meal.Dinner, Title = "Takeaway" });

        Assert.Equal(["Thu Upkeep Drill battery: Charge", "Fri Meal Dinner: Takeaway", "Sat Warranty Warranty ends: Drill battery"], Week(_kai));
    }

    [Fact]
    public void A_weekly_chore_repeats_through_a_month_and_a_monthly_one_once()
    {
        var weekly = CalendarFeed.Occurrences(new DateOnly(2026, 9, 30), new Recurrence(RecurrenceFrequency.Weekly), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal([7, 14, 21, 28], weekly.Select(day => day.Day));

        var monthly = CalendarFeed.Occurrences(new DateOnly(2026, 8, 31), new Recurrence(RecurrenceFrequency.Monthly), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal([new DateOnly(2026, 10, 30)], monthly);
    }

    [Fact]
    public void Events_are_checked()
    {
        Assert.Throws<ArgumentException>(() => Event(" ", Monday));
        Assert.Throws<ArgumentException>(() => Event("Backwards", Monday, endDate: Monday.AddDays(-1)));
        Assert.Throws<ArgumentException>(() => Event("Backwards", Monday, new TimeOnly(10, 0), new TimeOnly(9, 0)));
        Assert.Throws<ArgumentException>(() => Event("No start", Monday, end: new TimeOnly(9, 0)));

        var overnight = Event("Night shift", Monday, new TimeOnly(22, 0), new TimeOnly(6, 0), endDate: Monday.AddDays(1));
        Assert.Equal(Monday.AddDays(1), overnight.LastDay);
        Assert.Null(Event("One day", Monday, endDate: Monday).EndDate);
    }
}
