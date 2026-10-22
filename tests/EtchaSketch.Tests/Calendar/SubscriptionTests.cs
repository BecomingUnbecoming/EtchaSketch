using System.Globalization;
using EtchaSketch.Core.Calendar;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Calendar;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Calendar;

public sealed class SubscriptionTests : IDisposable
{
    private const string School = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:term@school
        DTSTART;VALUE=DATE:20261026
        DTEND;VALUE=DATE:20261031
        SUMMARY:Half term
        END:VEVENT
        BEGIN:VEVENT
        UID:club@school
        DTSTART:20261019T153000
        DTEND:20261019T163000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        SUMMARY:Chess club
        END:VEVENT
        BEGIN:VEVENT
        UID:club@school
        RECURRENCE-ID:20261102T153000
        DTSTART:20261103T153000
        DTEND:20261103T163000
        SUMMARY:Chess club (moved to Tuesday)
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-subscriptions-").FullName;
    private readonly SqliteConnection _db;
    private readonly SubscriptionRepository _subscriptions;
    private readonly CalendarFeed _feed;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;
    private readonly List<Uri> _fetched = [];

    public SubscriptionTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        var households = new HouseholdRepository(_db);
        var created = households.Create("Home", "Robin");
        households.AddMember(created.Id, "Kai");
        _home = households.Get(created.Id)!;
        (_robin, _kai) = (_home.Members[0], _home.Members[1]);
        _subscriptions = new SubscriptionRepository(_db);
        var inventory = new InventoryRepository(_db);
        var pantry = new PantryRepository(_db);
        _feed = new CalendarFeed(new TaskRepository(_db), new EventRepository(_db), inventory, new UpkeepRepository(_db, inventory), new MealRepository(_db, pantry),
            _subscriptions, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<string> Fetch(Uri url)
    {
        _fetched.Add(url);
        return url.Host switch
        {
            "school.example" => Task.FromResult(School),
            "down.example" => throw new HttpRequestException("No route to host"),
            _ => Task.FromResult("<html>Not a calendar</html>"),
        };
    }

    [Fact]
    public void Links_are_checked_and_webcal_means_https()
    {
        Assert.Equal("https://school.example/term.ics", SubscriptionRepository.Link(" webcal://school.example/term.ics ").ToString());
        Assert.Throws<ArgumentException>(() => SubscriptionRepository.Link("ftp://school.example/term.ics"));
        Assert.Throws<ArgumentException>(() => SubscriptionRepository.Link("school calendar"));
        Assert.Throws<ArgumentException>(() => _subscriptions.Add(_home.Id, _robin.Id, " ", "https://school.example/term.ics", shared: false));
    }

    [Fact]
    public async Task A_followed_calendar_shows_its_events_with_repeats_and_moved_days()
    {
        _subscriptions.Add(_home.Id, _robin.Id, "School", "https://school.example/term.ics", shared: false);

        var result = await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now);

        Assert.Equal((1, 0), (result.Fetched, result.Problems.Count));
        var entries = _feed.Between(_home, _robin.Id, new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 3))
            .Select(entry => $"{entry.Day:MM-dd} {entry.Start:HH\\:mm} {entry.Title} ({entry.Detail})".Replace("  ", " ", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "10-19 15:30 Chess club (School)",
            "10-26 Half term (School)", "10-26 15:30 Chess club (School)", "10-27 Half term (School)", "10-28 Half term (School)",
            "10-29 Half term (School)", "10-30 Half term (School)",
            "11-03 15:30 Chess club (moved to Tuesday) (School)",
        ], entries);
        Assert.All(_feed.Between(_home, _robin.Id, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19)), entry => Assert.Equal(EntryKind.Subscribed, entry.Kind));
    }

    [Fact]
    public async Task Calendars_are_fetched_at_most_every_six_hours_unless_asked()
    {
        _subscriptions.Add(_home.Id, _robin.Id, "School", "https://school.example/term.ics", shared: false);

        await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now);
        await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now.AddHours(5));
        Assert.Single(_fetched);

        await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now.AddHours(5), force: true);
        await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now.AddHours(12));
        Assert.Equal(3, _fetched.Count);
    }

    [Fact]
    public async Task A_link_that_fails_or_is_not_a_calendar_keeps_what_was_there()
    {
        var school = _subscriptions.Add(_home.Id, _robin.Id, "School", "https://school.example/term.ics", shared: false);
        await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now);
        _db.CreateCommand().Apply(command =>
        {
            command.CommandText = "UPDATE calendar_subscriptions SET url = 'https://down.example/term.ics'";
            command.ExecuteNonQuery();
        });
        _subscriptions.Add(_home.Id, _robin.Id, "Holidays", "https://elsewhere.example/page", shared: false);

        var result = await SubscriptionFetcher.RefreshAsync(_subscriptions, _robin.Id, Fetch, Now.AddDays(1));

        Assert.Equal(0, result.Fetched);
        Assert.Equal(["Holidays: that link doesn't give a calendar", "School: couldn't fetch it (No route to host)"], result.Problems);
        Assert.Equal(School, _subscriptions.Cached(school.Id)!.Value.Content);
    }

    [Fact]
    public async Task A_private_subscription_is_only_its_owners_and_a_shared_one_is_everyones()
    {
        _subscriptions.Add(_home.Id, _robin.Id, "My secret calendar", "https://school.example/private-token.ics", shared: false);
        _subscriptions.Add(_home.Id, _robin.Id, "School", "https://school.example/term.ics", shared: true);

        Assert.Equal(["School"], _subscriptions.VisibleTo(_kai.Id).Select(found => found.Name));
        await SubscriptionFetcher.RefreshAsync(_subscriptions, _kai.Id, Fetch, Now);
        Assert.Equal([new Uri("https://school.example/term.ics")], _fetched);
    }
}

internal static class CommandExtensions
{
    public static void Apply(this SqliteCommand command, Action<SqliteCommand> action)
    {
        using (command)
        {
            action(command);
        }
    }
}
