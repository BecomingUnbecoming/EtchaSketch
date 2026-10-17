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
    private readonly Profile _lacy;
    private readonly Profile _jordy;

    public RemindersTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);

        var households = new HouseholdRepository(_db);
        var created = households.Create("Home", "Lacy");
        _lacy = created.Members[0];
        _jordy = households.AddMember(created.Id, "Jordy");
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
            OwnerId = (owner ?? _lacy).Id,
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

        var reminder = Assert.Single(FeedOf(_lacy, Wednesday.AddHours(1)));
        Assert.Equal("Book dentist is due today", reminder.Title);
        Assert.Equal(NotificationKind.DueToday, reminder.Kind);
    }

    [Fact]
    public void An_overdue_task_says_when_it_was_due()
    {
        Add("Renew passport", Today.AddDays(-2));

        _reminders.Check(_home, Wednesday);

        var reminder = Assert.Single(FeedOf(_lacy));
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
        Assert.Empty(FeedOf(_lacy));
    }

    [Fact]
    public void A_chore_assigned_to_someone_reminds_them_and_not_its_owner()
    {
        Add("Dishes", Today, PrivacyTier.Household, assignee: _jordy);

        _reminders.Check(_home, Wednesday);

        Assert.Empty(FeedOf(_lacy));
        var titles = FeedOf(_jordy).Select(notification => notification.Title).ToList();
        Assert.Contains("Lacy assigned Dishes to you", titles);
        Assert.Contains("Dishes is due today", titles);
    }

    [Fact]
    public void Someone_elses_private_task_never_reaches_your_feed()
    {
        Add("Surprise party", Today);

        _reminders.Check(_home, Wednesday);

        Assert.Empty(FeedOf(_jordy));
    }

    [Fact]
    public void A_reminder_keeps_the_privacy_of_its_task()
    {
        Add("Book dentist", Today);
        Add("Dishes", Today, PrivacyTier.Household, assignee: _jordy);
        Add("Stream schedule", Today, PrivacyTier.Public);

        _reminders.Check(_home, Wednesday);

        Assert.Equal(PrivacyTier.Private, FeedOf(_lacy).Single(n => n.Title == "Book dentist is due today").Tier);
        Assert.All(FeedOf(_jordy), notification => Assert.Equal(PrivacyTier.Household, notification.Tier));
        Assert.Equal(PrivacyTier.Household, FeedOf(_lacy).Single(n => n.Title == "Stream schedule is due today").Tier);
        Assert.All(FeedOf(_lacy), notification => Assert.Null(PrivacyGateway.ForStudio(notification)));
    }

    [Fact]
    public void A_repeating_chore_reminds_again_on_its_next_due_date()
    {
        var bins = Add("Bins", Today, PrivacyTier.Household, recurrence: new Recurrence(RecurrenceFrequency.Weekly));
        _reminders.Check(_home, Wednesday);
        _tasks.Complete(bins.Id, _lacy.Id, Wednesday);

        _reminders.Check(_home, Wednesday.AddDays(7));

        Assert.Equal(2, FeedOf(_lacy, Wednesday.AddDays(7)).Count(n => n.Title == "Bins is due today"));
    }

    [Fact]
    public void Finished_tasks_stay_quiet()
    {
        var task = Add("Book dentist", Today);
        _tasks.Complete(task.Id, _lacy.Id, Wednesday);

        _reminders.Check(_home, Wednesday);

        Assert.Empty(FeedOf(_lacy));
    }
}
