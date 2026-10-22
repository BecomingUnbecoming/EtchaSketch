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

public sealed class NotificationRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Evening = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-notifications-").FullName;
    private readonly SqliteConnection _db;
    private readonly NotificationRepository _notifications;
    private readonly TaskRepository _tasks;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;

    public NotificationRepositoryTests()
    {
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);

        var households = new HouseholdRepository(_db);
        _home = households.Create("Home", "Robin");
        _robin = _home.Members[0];
        _kai = households.AddMember(_home.Id, "Kai");
        _notifications = new NotificationRepository(_db);
        _tasks = new TaskRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Notification For(Profile recipient, string title, DateTimeOffset? at = null, PrivacyTier tier = PrivacyTier.Private) => new()
    {
        OwnerId = recipient.Id,
        Kind = NotificationKind.DueToday,
        Title = title,
        CreatedAt = at ?? Evening,
        Tier = tier,
    };

    [Fact]
    public void The_feed_shows_your_notifications_newest_first()
    {
        _notifications.Add(For(_robin, "first", Evening), "a");
        _notifications.Add(For(_robin, "second", Evening.AddMinutes(5)), "b");

        var feed = _notifications.Feed(_robin.Id, Evening.AddHours(1));

        Assert.Equal(["second", "first"], feed.Shown.Select(notification => notification.Title));
        Assert.Equal(0, feed.HeldCount);
        Assert.Equal(2, _notifications.UnreadCount(_robin.Id, Evening.AddHours(1)));
    }

    [Fact]
    public void The_same_reminder_is_only_added_once()
    {
        Assert.NotNull(_notifications.Add(For(_robin, "Dishes is due today"), "due:dishes"));
        Assert.Null(_notifications.Add(For(_robin, "Dishes is due today"), "due:dishes"));

        Assert.Single(_notifications.Feed(_robin.Id, Evening).Shown);
    }

    [Fact]
    public void Each_person_sees_only_their_own_notifications()
    {
        _notifications.Add(For(_robin, "for Robin"), "a");

        Assert.Empty(_notifications.Feed(_kai.Id, Evening).Shown);
        Assert.Equal(0, _notifications.UnreadCount(_kai.Id, Evening));
    }

    [Fact]
    public void Quiet_hours_hold_notifications_until_they_end()
    {
        _notifications.SetQuietHours(_robin.Id, new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        var lateNight = new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.Zero);
        var morning = new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);

        var added = _notifications.Add(For(_robin, "Bins go out tomorrow", lateNight), "a")!;

        Assert.Equal(morning, added.DeliverAt);
        var night = _notifications.Feed(_robin.Id, lateNight.AddMinutes(30));
        Assert.Empty(night.Shown);
        Assert.Equal(1, night.HeldCount);
        Assert.Equal(morning, night.HeldUntil);
        Assert.Equal(0, _notifications.UnreadCount(_robin.Id, lateNight.AddMinutes(30)));

        Assert.Single(_notifications.Feed(_robin.Id, morning).Shown);
    }

    [Fact]
    public void Quiet_hours_belong_to_one_person()
    {
        _notifications.SetQuietHours(_robin.Id, new QuietHours(new TimeOnly(0, 0), new TimeOnly(23, 59)));

        Assert.Equal(Evening, _notifications.Add(For(_kai, "for Kai"), "a")!.DeliverAt);
        Assert.Null(_notifications.QuietHoursFor(_kai.Id));
    }

    [Fact]
    public void Quiet_hours_can_be_changed_and_turned_off()
    {
        var hours = new QuietHours(new TimeOnly(21, 30), new TimeOnly(6, 45));
        _notifications.SetQuietHours(_robin.Id, hours);
        Assert.Equal(hours, _notifications.QuietHoursFor(_robin.Id));

        _notifications.SetQuietHours(_robin.Id, null);
        Assert.Null(_notifications.QuietHoursFor(_robin.Id));
    }

    [Fact]
    public void Marking_all_read_leaves_held_notifications_unread()
    {
        _notifications.SetQuietHours(_robin.Id, new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        _notifications.Add(For(_robin, "now"), "a");
        _notifications.Add(For(_robin, "later", Evening.AddHours(5)), "b");

        _notifications.MarkAllRead(_robin.Id, Evening.AddHours(5));

        Assert.NotNull(_notifications.Feed(_robin.Id, Evening.AddHours(5)).Shown.Single().ReadAt);
        var morning = _notifications.Feed(_robin.Id, Evening.AddDays(1));
        Assert.Null(morning.Shown.Single(notification => notification.Title == "later").ReadAt);
        Assert.Equal(1, _notifications.UnreadCount(_robin.Id, Evening.AddDays(1)));
    }

    [Fact]
    public void A_notification_is_never_stored_as_public()
    {
        var added = _notifications.Add(For(_robin, "Stream starts at 8", tier: PrivacyTier.Public), "a")!;

        var stored = _notifications.Feed(_robin.Id, Evening).Shown.Single();
        Assert.Equal(PrivacyTier.Household, added.Tier);
        Assert.Equal(PrivacyTier.Household, stored.Tier);
        Assert.Null(PrivacyGateway.ForStudio(stored));
    }

    [Fact]
    public void Someone_elses_private_notification_is_withheld_but_a_household_one_shows()
    {
        var mine = _notifications.Add(For(_robin, "Book dentist is due today"), "a")!;
        var shared = _notifications.Add(For(_robin, "Dishes is due today", tier: PrivacyTier.Household), "b")!;

        Assert.Null(PrivacyGateway.Project(mine, Audience.ForProfile(_kai.Id)));
        Assert.Equal("Dishes is due today", PrivacyGateway.Project(shared, Audience.ForProfile(_kai.Id))!["Title"]);
        Assert.Equal("Book dentist is due today", PrivacyGateway.Project(mine, Audience.ForProfile(_robin.Id))!["Title"]);
    }

    [Fact]
    public void Deleting_a_task_removes_its_notifications()
    {
        var task = _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Book dentist" });
        _notifications.Add(For(_robin, "Book dentist is due today") with { TaskId = task.Id }, "a");

        _tasks.Delete(task.Id);

        Assert.Empty(_notifications.Feed(_robin.Id, Evening).Shown);
    }

    [Fact]
    public void Notifications_need_someone_to_go_to()
    {
        var stranger = For(_robin, "x") with { OwnerId = Guid.NewGuid() };

        Assert.Throws<ArgumentException>(() => _notifications.Add(stranger, "a"));
    }

    [Fact]
    public void A_kind_from_a_newer_version_is_shown_rather_than_breaking_the_feed()
    {
        _notifications.Add(For(_robin, "Oil change is due"), "from-the-future");
        using (var command = _db.CreateCommand())
        {
            command.CommandText = "UPDATE notifications SET kind = 'SomethingAddedLater'";
            command.ExecuteNonQuery();
        }

        var shown = Assert.Single(_notifications.Feed(_robin.Id, Evening).Shown);

        Assert.Equal(("Oil change is due", NotificationKind.Other), (shown.Title, shown.Kind));
    }
}
