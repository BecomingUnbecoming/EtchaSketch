using System.Globalization;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Notifications;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Notifications;

public sealed class RemindersTests : IDisposable
{
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-reminders-").FullName;
    private readonly SqliteConnection _db;
    private readonly TaskRepository _tasks;
    private readonly NotificationRepository _notifications;
    private readonly Reminders _reminders;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;

    public RemindersTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);

        var households = new HouseholdRepository(_db);
        var created = households.Create("Home", "Robin");
        _robin = created.Members[0];
        _kai = households.AddMember(created.Id, "Kai");
        _home = households.Get(created.Id)!;
        _tasks = new TaskRepository(_db);
        _notifications = new NotificationRepository(_db);
        _reminders = new Reminders(_tasks, _notifications);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private TaskItem Add(string title, DateOnly? due, PrivacyTier tier = PrivacyTier.Private, Profile? owner = null, Profile? assignee = null,
        Recurrence? recurrence = null) =>
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id,
            OwnerId = (owner ?? _robin).Id,
            Title = title,
            Due = due,
            Tier = tier,
            AssigneeId = assignee?.Id,
            Recurrence = recurrence,
        });

    private IReadOnlyList<Notification> FeedOf(Profile profile, DateTimeOffset? at = null) =>
        _notifications.Feed(profile.Id, at ?? Wednesday).Shown;

    [Fact]
    public void A_task_due_today_reminds_its_owner_once()
    {
        Add("Book dentist", Today);

        _reminders.Check(_home, Wednesday);
        _reminders.Check(_home, Wednesday.AddHours(1));

        var reminder = Assert.Single(FeedOf(_robin, Wednesday.AddHours(1)));
        Assert.Equal("Book dentist is due today", reminder.Title);
        Assert.Equal(NotificationKind.DueToday, reminder.Kind);
    }

    [Fact]
    public void An_overdue_task_says_when_it_was_due()
    {
        Add("Renew passport", Today.AddDays(-2));

        _reminders.Check(_home, Wednesday);

        var reminder = Assert.Single(FeedOf(_robin));
        Assert.Equal("Renew passport is overdue", reminder.Title);
        Assert.Equal("It was due Mon 19 Oct", reminder.Body);
        Assert.Equal(NotificationKind.Overdue, reminder.Kind);
    }

    [Fact]
    public void Tasks_without_a_due_date_or_due_later_stay_quiet()
    {
        Add("Someday", null);
        Add("Next week", Today.AddDays(7));

        Assert.Equal(0, _reminders.Check(_home, Wednesday));
        Assert.Empty(FeedOf(_robin));
    }

    [Fact]
    public void A_chore_assigned_to_someone_reminds_them_and_not_its_owner()
    {
        Add("Dishes", Today, PrivacyTier.Household, assignee: _kai);

        _reminders.Check(_home, Wednesday);

        Assert.Empty(FeedOf(_robin));
        var titles = FeedOf(_kai).Select(notification => notification.Title).ToList();
        Assert.Contains("Robin assigned Dishes to you", titles);
        Assert.Contains("Dishes is due today", titles);
    }

    [Fact]
    public void Someone_elses_private_task_never_reaches_your_feed()
    {
        Add("Surprise party", Today);

        _reminders.Check(_home, Wednesday);

        Assert.Empty(FeedOf(_kai));
    }

    [Fact]
    public void A_reminder_keeps_the_privacy_of_its_task()
    {
        Add("Book dentist", Today);
        Add("Dishes", Today, PrivacyTier.Household, assignee: _kai);
        Add("Stream schedule", Today, PrivacyTier.Public);

        _reminders.Check(_home, Wednesday);

        Assert.Equal(PrivacyTier.Private, FeedOf(_robin).Single(n => n.Title == "Book dentist is due today").Tier);
        Assert.All(FeedOf(_kai), notification => Assert.Equal(PrivacyTier.Household, notification.Tier));
        Assert.Equal(PrivacyTier.Household, FeedOf(_robin).Single(n => n.Title == "Stream schedule is due today").Tier);
        Assert.All(FeedOf(_robin), notification => Assert.Null(PrivacyGateway.ForStudio(notification)));
    }

    [Fact]
    public void A_repeating_chore_reminds_again_on_its_next_due_date()
    {
        var bins = Add("Bins", Today, PrivacyTier.Household, recurrence: new Recurrence(RecurrenceFrequency.Weekly));
        _reminders.Check(_home, Wednesday);
        _tasks.Complete(bins.Id, _robin.Id, Wednesday);

        _reminders.Check(_home, Wednesday.AddDays(7));

        Assert.Equal(2, FeedOf(_robin, Wednesday.AddDays(7)).Count(n => n.Title == "Bins is due today"));
    }

    [Fact]
    public void Finished_tasks_stay_quiet()
    {
        var task = Add("Book dentist", Today);
        _tasks.Complete(task.Id, _robin.Id, Wednesday);

        _reminders.Check(_home, Wednesday);

        Assert.Empty(FeedOf(_robin));
    }

    [Theory]
    [InlineData(1, "Rotate keys is due tomorrow")]
    [InlineData(3, "Rotate keys is due in 3 days")]
    public void An_early_reminder_comes_once_as_the_day_approaches(int daysAway, string title)
    {
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id,
            OwnerId = _robin.Id,
            Title = "Rotate keys",
            Due = Today.AddDays(daysAway),
            RemindDaysBefore = 3,
        });

        Assert.Equal(1, _reminders.Check(_home, Wednesday));
        Assert.Equal(0, _reminders.Check(_home, Wednesday));
        Assert.Equal(title, FeedOf(_robin).Single().Title);
    }

    [Fact]
    public void No_early_reminder_comes_before_its_time_or_when_none_was_asked_for()
    {
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Far off", Due = Today.AddDays(4), RemindDaysBefore = 3 });
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Not asked", Due = Today.AddDays(1) });

        Assert.Equal(0, _reminders.Check(_home, Wednesday));
    }

    private TaskItem AddTimed(string title, DateOnly due, TimeOnly time, int? minutesBefore = null, int? daysBefore = null, bool alarm = false) =>
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id,
            OwnerId = _robin.Id,
            Title = title,
            Due = due,
            DueTime = time,
            RemindMinutesBefore = minutesBefore,
            RemindDaysBefore = daysBefore,
            Alarm = alarm,
        });

    private static DateTimeOffset At(DateOnly day, int hour, int minute, TimeSpan? offset = null) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), offset ?? TimeSpan.Zero);

    [Fact]
    public void A_task_with_a_time_waits_for_that_time_then_reminds_once()
    {
        AddTimed("Call the vet", Today, new TimeOnly(14, 5));

        Assert.Equal(0, _reminders.Check(_home, At(Today, 9, 0)));
        Assert.Equal(0, _reminders.Check(_home, At(Today, 14, 4)));
        Assert.Equal(1, _reminders.Check(_home, At(Today, 14, 5)));
        Assert.Equal(0, _reminders.Check(_home, At(Today, 14, 30)));

        var reminder = Assert.Single(FeedOf(_robin, At(Today, 14, 30)));
        Assert.Equal("Call the vet is due now", reminder.Title);
        Assert.Equal("Due at 14:05", reminder.Body);
        Assert.Equal(NotificationKind.DueNow, reminder.Kind);
    }

    [Fact]
    public void A_reminder_minutes_before_comes_ahead_of_the_due_time()
    {
        AddTimed("Leave for the dentist", Today, new TimeOnly(15, 0), minutesBefore: 30);

        Assert.Equal(0, _reminders.Check(_home, At(Today, 14, 29)));
        Assert.Equal(1, _reminders.Check(_home, At(Today, 14, 30)));
        Assert.Equal(1, _reminders.Check(_home, At(Today, 15, 0)));

        var feed = FeedOf(_robin, At(Today, 15, 0));
        Assert.Contains(feed, n => n is { Kind: NotificationKind.DueShortly, Title: "Leave for the dentist is due at 15:00", Body: "In 30 minutes" });
        Assert.Contains(feed, n => n.Kind == NotificationKind.DueNow);
    }

    [Fact]
    public void Opening_the_app_late_still_gives_the_due_reminder_but_not_the_one_before()
    {
        AddTimed("Take medicine", Today, new TimeOnly(8, 0), minutesBefore: 15);

        Assert.Equal(1, _reminders.Check(_home, At(Today, 11, 0)));
        Assert.Equal(NotificationKind.DueNow, Assert.Single(FeedOf(_robin, At(Today, 11, 0))).Kind);
    }

    [Fact]
    public void An_early_reminder_on_a_timed_task_comes_at_that_time_of_day()
    {
        AddTimed("Pay rent", Today.AddDays(2), new TimeOnly(18, 0), daysBefore: 2);

        Assert.Equal(0, _reminders.Check(_home, At(Today, 9, 0)));
        Assert.Equal(1, _reminders.Check(_home, At(Today, 18, 0)));
        Assert.Equal("Pay rent is due in 2 days", FeedOf(_robin, At(Today, 18, 0)).Single().Title);
    }

    [Fact]
    public void A_timed_task_fires_at_the_same_clock_time_on_both_sides_of_a_clock_change()
    {
        // US clocks go back on Sunday 1 November 2026: -07:00 before, -08:00 after.
        var sunday = new DateOnly(2026, 11, 1);
        AddTimed("Water plants", sunday, new TimeOnly(14, 5));
        var winter = TimeSpan.FromHours(-8);

        Assert.Equal(0, _reminders.Check(_home, At(sunday, 13, 5, winter)));
        Assert.Equal(1, _reminders.Check(_home, At(sunday, 14, 5, winter)));
    }

    [Fact]
    public void An_alarm_comes_through_quiet_hours_but_a_plain_reminder_waits()
    {
        _notifications.SetQuietHours(_robin.Id, new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        AddTimed("Wake up", Today, new TimeOnly(6, 30), alarm: true);
        AddTimed("Check email", Today, new TimeOnly(6, 30));

        _reminders.Check(_home, At(Today, 6, 30));

        var shown = Assert.Single(FeedOf(_robin, At(Today, 6, 31)));
        Assert.Equal("Wake up is due now", shown.Title);
        Assert.Equal(2, FeedOf(_robin, At(Today, 7, 0)).Count);
    }

    [Fact]
    public void A_snoozed_reminder_comes_back_later_and_only_once()
    {
        AddTimed("Call the vet", Today, new TimeOnly(14, 0));
        _reminders.Check(_home, At(Today, 14, 0));
        var original = Assert.Single(FeedOf(_robin, At(Today, 14, 0)));

        _notifications.Snooze(original.Id, At(Today, 14, 10), At(Today, 14, 0));
        _reminders.Check(_home, At(Today, 14, 5));

        Assert.NotNull(Assert.Single(FeedOf(_robin, At(Today, 14, 9))).ReadAt);
        var back = FeedOf(_robin, At(Today, 14, 10));
        Assert.Equal(2, back.Count);
        Assert.Contains(back, n => n.ReadAt is null && n.Title == "Call the vet is due now");
        Assert.Throws<ArgumentException>(() => _notifications.Snooze(original.Id, At(Today, 13, 0), At(Today, 14, 0)));
    }

    private IReadOnlyList<PlannedReminder> Upcoming(DateTimeOffset now, int days = 7) =>
        _reminders.Upcoming(_home, _robin.Id, now, TimeSpan.FromDays(days));

    [Fact]
    public void Upcoming_lists_timed_reminders_at_their_times_in_order()
    {
        AddTimed("Vet", Today, new TimeOnly(15, 0), minutesBefore: 30, alarm: true);
        AddTimed("Rent", Today.AddDays(2), new TimeOnly(18, 0), daysBefore: 1);

        // Overdue follow-ups (the next morning) are covered by their own test.
        var planned = Upcoming(At(Today, 9, 0)).Where(p => p.Kind != NotificationKind.Overdue).ToList();

        Assert.Equal(
            [
                (At(Today, 14, 30).DateTime, NotificationKind.DueShortly, false),
                (At(Today, 15, 0).DateTime, NotificationKind.DueNow, true),
                (At(Today.AddDays(1), 18, 0).DateTime, NotificationKind.DueSoon, false),
                (At(Today.AddDays(2), 18, 0).DateTime, NotificationKind.DueNow, false),
            ],
            planned.Select(p => (p.At, p.Kind, p.Alarm)));
        Assert.Equal("Vet is due at 15:00", planned[0].Title);
        Assert.Equal("In 30 minutes", planned[0].Body);
        Assert.Equal("Rent is due tomorrow", planned[2].Title);
    }

    [Fact]
    public void Upcoming_puts_reminders_without_a_time_in_the_morning_and_overdue_ones_the_next_morning()
    {
        Add("Bins", Today.AddDays(1));

        var planned = Upcoming(At(Today, 9, 0));

        Assert.Equal(
            [
                (At(Today.AddDays(1), 8, 0).DateTime, NotificationKind.DueToday),
                (At(Today.AddDays(2), 8, 0).DateTime, NotificationKind.Overdue),
            ],
            planned.Select(p => (p.At, p.Kind)));
    }

    [Fact]
    public void Upcoming_leaves_out_what_has_passed_what_is_too_far_off_and_other_peoples_tasks()
    {
        AddTimed("Earlier today", Today, new TimeOnly(8, 0));
        AddTimed("Next month", Today.AddDays(30), new TimeOnly(9, 0));
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _kai.Id, Title = "Kai's", Due = Today, DueTime = new TimeOnly(12, 0) });

        var planned = Upcoming(At(Today, 9, 0));

        Assert.DoesNotContain(planned, p => p.Title.StartsWith("Next month", StringComparison.Ordinal));
        Assert.DoesNotContain(planned, p => p.Title.StartsWith("Kai", StringComparison.Ordinal));
        Assert.DoesNotContain(planned, p => p.Kind == NotificationKind.DueNow && p.Title.StartsWith("Earlier", StringComparison.Ordinal));
    }

    [Fact]
    public void Upcoming_waits_for_quiet_hours_to_end_except_for_alarms()
    {
        _notifications.SetQuietHours(_robin.Id, new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        AddTimed("Wake up", Today.AddDays(1), new TimeOnly(6, 30), alarm: true);
        AddTimed("Check email", Today.AddDays(1), new TimeOnly(6, 30));
        Add("Bins", Today.AddDays(1));

        var planned = Upcoming(At(Today, 9, 0)).Where(p => p.Kind != NotificationKind.Overdue).ToList();

        var tomorrow = Today.AddDays(1);
        Assert.Contains(planned, p => p is { Title: "Wake up is due now", Alarm: true } && p.At == At(tomorrow, 6, 30).DateTime);
        Assert.Contains(planned, p => p.Title == "Check email is due now" && p.At == At(tomorrow, 7, 0).DateTime);
        Assert.Contains(planned, p => p.Title == "Bins is due today" && p.At == At(tomorrow, 7, 0).DateTime);
    }

    [Fact]
    public void A_planned_reminder_and_the_feed_entry_for_it_share_a_key()
    {
        AddTimed("Vet", Today, new TimeOnly(15, 0));
        var planned = Assert.Single(Upcoming(At(Today, 9, 0)), p => p.Kind == NotificationKind.DueNow);

        _reminders.Check(_home, At(Today, 15, 0));

        Assert.Equal(0, _notifications.Add(new Notification
        {
            OwnerId = _robin.Id, Kind = planned.Kind, Title = planned.Title, CreatedAt = At(Today, 15, 1),
        }, planned.Key) is null ? 0 : 1);
    }
}
