using System.Text;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Notifications;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Sync;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Sync;

/// <summary>Several devices, each with its own encrypted database, syncing through one real server.</summary>
public sealed class SyncEngineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private readonly List<Device> _devices = [];
    private SyncServerFixture _server = null!;
    private Device _desktop = null!;
    private Household _home = null!;
    private Profile _robin = null!;
    private Profile _kai = null!;

    public async Task InitializeAsync()
    {
        _server = await SyncServerFixture.StartAsync();
        _desktop = NewDevice("robin-desktop");
        var created = _desktop.Households.Create("Home", "Robin");
        _robin = created.Members[0];
        _kai = _desktop.Households.AddMember(created.Id, "Kai");
        _home = _desktop.Households.Get(created.Id)!;
        _desktop.Households.SetCurrentProfile(_robin.Id);
        _desktop.Sync.Enable(_home.Id);
    }

    public async Task DisposeAsync()
    {
        foreach (var device in _devices)
        {
            device.Dispose();
        }

        await _server.DisposeAsync();
    }

    private Device NewDevice(string name)
    {
        var device = new Device(name, _server.Client());
        _devices.Add(device);
        return device;
    }

    private async Task<Device> JoinAs(Profile profile, string name)
    {
        var code = await _desktop.Sync.InviteAsync(profile.Id);
        var device = NewDevice(name);
        Assert.Equal(profile.Id, await device.Sync.JoinAsync(code));
        return device;
    }

    private TaskItem Add(Device device, string title, PrivacyTier tier = PrivacyTier.Private, Profile? owner = null,
        Profile? assignee = null, DateOnly? due = null, string? notes = null) =>
        device.Tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id,
            OwnerId = (owner ?? _robin).Id,
            Title = title,
            Tier = tier,
            AssigneeId = assignee?.Id,
            Due = due,
            Notes = notes,
            Recurrence = tier == PrivacyTier.Household ? new Recurrence(RecurrenceFrequency.Weekly) : null,
        });

    private static IEnumerable<string> TitlesFor(Device device, Profile profile) =>
        device.Tasks.VisibleTo(profile.Id).Select(task => task.Title).Order();

    [Fact]
    public async Task Your_phone_gets_the_household_and_all_your_tasks()
    {
        Add(_desktop, "Book dentist", notes: "ask about the crown");
        Add(_desktop, "Dishes", PrivacyTier.Household, assignee: _kai);
        await _desktop.Sync.SyncAsync();

        var phone = await JoinAs(_robin, "robin-phone");

        Assert.Equal(["Robin", "Kai"], phone.Households.Get(_home.Id)!.Members.Select(member => member.DisplayName));
        Assert.Equal(_robin.Id, phone.Households.CurrentProfileId());
        Assert.Equal(["Book dentist", "Dishes"], TitlesFor(phone, _robin));
        Assert.Equal("ask about the crown", phone.Tasks.VisibleTo(_robin.Id).Single(task => task.Title == "Book dentist").Notes);
    }

    [Fact]
    public async Task A_recovery_key_brings_everything_back_on_an_empty_device_after_every_device_is_lost()
    {
        Add(_desktop, "Book dentist", notes: "ask about the crown");
        Add(_desktop, "Dishes", PrivacyTier.Household, assignee: _kai);
        await _desktop.Sync.SyncAsync();
        var recoveryKey = _desktop.Sync.CreateRecoveryKey("a long passphrase", iterations: 1_000);

        // Every device is gone; only the server's copy and the saved text remain.
        var replacement = NewDevice("robin-new-pc");
        var profileId = await replacement.Sync.RestoreAsync(recoveryKey, "a long passphrase");

        Assert.Equal(_robin.Id, profileId);
        Assert.Equal(_robin.Id, replacement.Households.CurrentProfileId());
        Assert.Equal(["Book dentist", "Dishes"], TitlesFor(replacement, _robin));
        Assert.Equal("ask about the crown", replacement.Tasks.VisibleTo(_robin.Id).Single(task => task.Title == "Book dentist").Notes);
    }

    [Fact]
    public async Task A_wrong_passphrase_restores_nothing()
    {
        await _desktop.Sync.SyncAsync();
        var recoveryKey = _desktop.Sync.CreateRecoveryKey("a long passphrase", iterations: 1_000);

        var replacement = NewDevice("robin-new-pc");
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => replacement.Sync.RestoreAsync(recoveryKey, "not the passphrase"));

        Assert.Empty(replacement.Households.All());
    }

    [Fact]
    public async Task A_device_that_already_has_a_household_refuses_a_recovery_key()
    {
        var recoveryKey = _desktop.Sync.CreateRecoveryKey("a long passphrase", iterations: 1_000);
        var other = NewDevice("other");
        other.Households.Create("Other home", "Someone");

        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Sync.RestoreAsync(recoveryKey, "a long passphrase"));
    }

    [Fact]
    public void Sync_off_has_no_keys_to_keep()
    {
        var lonely = NewDevice("lonely");
        var made = lonely.Households.Create("Home", "Robin");
        lonely.Households.SetCurrentProfile(made.Members[0].Id);

        Assert.Throws<InvalidOperationException>(() => lonely.Sync.CreateRecoveryKey("a long passphrase", iterations: 1_000));
    }

    [Fact]
    public async Task Project_labels_and_the_early_reminder_travel_with_the_task()
    {
        _desktop.Tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Rotate keys", Due = Today.AddDays(5), Project = "Homelab",
            Labels = ["b-hands", "security"], RemindDaysBefore = 2,
        });
        await _desktop.Sync.SyncAsync();

        var phone = await JoinAs(_robin, "robin-phone");

        var task = phone.Tasks.VisibleTo(_robin.Id).Single();
        Assert.Equal("Homelab", task.Project);
        Assert.Equal(["b-hands", "security"], task.Labels);
        Assert.Equal(2, task.RemindDaysBefore);
    }

    [Fact]
    public async Task Kais_phone_never_receives_robins_private_tasks_or_notes()
    {
        Add(_desktop, "Surprise party for Kai", notes: "the bakery on 5th");
        Add(_desktop, "Dishes", PrivacyTier.Household, assignee: _kai, notes: "Robin's note");
        await _desktop.Sync.SyncAsync();

        var kaisPhone = await JoinAs(_kai, "kai-phone");

        Assert.Equal(["Dishes"], TitlesFor(kaisPhone, _kai));
        Assert.Null(kaisPhone.Tasks.VisibleTo(_kai.Id).Single().Notes);
        Assert.False(kaisPhone.Contains("Surprise party"));
        Assert.False(kaisPhone.Contains("the bakery on 5th"));
        Assert.False(kaisPhone.Contains("Robin's note"));
    }

    [Fact]
    public async Task Changes_made_on_one_device_reach_the_others()
    {
        var dishes = Add(_desktop, "Dishes", PrivacyTier.Household, assignee: _kai, due: Today);
        await _desktop.Sync.SyncAsync();
        var kaisPhone = await JoinAs(_kai, "kai-phone");

        kaisPhone.Tasks.Complete(dishes.Id, _kai.Id, Wednesday);
        await kaisPhone.Sync.SyncAsync();
        await _desktop.Sync.SyncAsync();

        Assert.Equal(Today.AddDays(7), _desktop.Tasks.Get(dishes.Id)!.Due);
        Assert.Equal(_kai.Id, Assert.Single(_desktop.Tasks.Completions(dishes.Id)).ProfileId);
    }

    [Fact]
    public async Task Deleting_a_task_deletes_it_everywhere()
    {
        var dishes = Add(_desktop, "Dishes", PrivacyTier.Household);
        await _desktop.Sync.SyncAsync();
        var phone = await JoinAs(_robin, "robin-phone");

        phone.Tasks.Delete(dishes.Id);
        await phone.Sync.SyncAsync();
        await _desktop.Sync.SyncAsync();

        Assert.Null(_desktop.Tasks.Get(dishes.Id));
    }

    [Fact]
    public async Task Making_a_shared_task_private_takes_it_off_other_peoples_devices()
    {
        var gift = Add(_desktop, "Gift ideas", PrivacyTier.Household);
        await _desktop.Sync.SyncAsync();
        var kaisPhone = await JoinAs(_kai, "kai-phone");
        var robinsPhone = await JoinAs(_robin, "robin-phone");
        Assert.Equal(["Gift ideas"], TitlesFor(kaisPhone, _kai));

        _desktop.Tasks.Update(_desktop.Tasks.Get(gift.Id)! with { Tier = PrivacyTier.Private, Recurrence = null });
        await _desktop.Sync.SyncAsync();
        await kaisPhone.Sync.SyncAsync();
        await robinsPhone.Sync.SyncAsync();

        Assert.Empty(TitlesFor(kaisPhone, _kai));
        Assert.Null(kaisPhone.Tasks.Get(gift.Id));
        Assert.Equal(PrivacyTier.Private, robinsPhone.Tasks.Get(gift.Id)!.Tier);
    }

    [Fact]
    public async Task When_two_devices_change_the_same_task_the_later_change_wins()
    {
        var task = Add(_desktop, "Paint fence");
        await _desktop.Sync.SyncAsync();
        var phone = await JoinAs(_robin, "robin-phone");

        _desktop.Tasks.Update(_desktop.Tasks.Get(task.Id)! with { Title = "Paint the fence" });
        await _desktop.Sync.SyncAsync();
        phone.Clock.Advance(TimeSpan.FromMinutes(1));
        phone.Tasks.Update(phone.Tasks.Get(task.Id)! with { Title = "Paint the fence blue" });
        await phone.Sync.SyncAsync();
        await _desktop.Sync.SyncAsync();

        Assert.Equal("Paint the fence blue", _desktop.Tasks.Get(task.Id)!.Title);
        Assert.Equal("Paint the fence blue", phone.Tasks.Get(task.Id)!.Title);
    }

    [Fact]
    public async Task The_server_never_holds_anything_readable()
    {
        Add(_desktop, "Book dentist", notes: "ask about the crown");
        Add(_desktop, "Dishes", PrivacyTier.Household);
        _desktop.Notifications.SetQuietHours(_robin.Id, new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        await _desktop.Sync.SyncAsync();
        await _desktop.Sync.InviteAsync(_kai.Id);
        _server.Store.Dispose();

        var stored = Encoding.UTF8.GetString(File.ReadAllBytes(_server.StorePath))
            + string.Concat(Directory.EnumerateFiles(_server.Directory).Select(file => Encoding.UTF8.GetString(File.ReadAllBytes(file))));
        foreach (var secret in new[] { "Book dentist", "crown", "Dishes", "Robin", "Kai", "Home", _robin.Id.ToString(), _home.Id.ToString() })
        {
            Assert.DoesNotContain(secret, stored);
        }
    }

    [Fact]
    public async Task A_pairing_code_works_only_once()
    {
        var code = await _desktop.Sync.InviteAsync(_kai.Id);
        await NewDevice("kai-phone").Sync.JoinAsync(code);

        await Assert.ThrowsAsync<PairingCodeNotFoundException>(() => NewDevice("intruder").Sync.JoinAsync(code));
    }

    [Fact]
    public async Task Reminders_raised_on_two_devices_show_once()
    {
        Add(_desktop, "Book dentist", due: Today);
        await _desktop.Sync.SyncAsync();
        var phone = await JoinAs(_robin, "robin-phone");

        _desktop.Reminders.Check(_desktop.Households.Get(_home.Id)!, Wednesday);
        phone.Reminders.Check(phone.Households.Get(_home.Id)!, Wednesday);
        await _desktop.Sync.SyncAsync();
        await phone.Sync.SyncAsync();
        await _desktop.Sync.SyncAsync();

        Assert.Single(_desktop.Notifications.Feed(_robin.Id, Wednesday).Shown);
        Assert.Single(phone.Notifications.Feed(_robin.Id, Wednesday).Shown);
    }

    [Fact]
    public async Task Quiet_hours_follow_you_to_your_other_devices_but_not_to_others()
    {
        var hours = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0));
        _desktop.Notifications.SetQuietHours(_robin.Id, hours);
        await _desktop.Sync.SyncAsync();

        var robinsPhone = await JoinAs(_robin, "robin-phone");
        var kaisPhone = await JoinAs(_kai, "kai-phone");

        Assert.Equal(hours, robinsPhone.Notifications.QuietHoursFor(_robin.Id));
        Assert.Null(kaisPhone.Notifications.QuietHoursFor(_robin.Id));
    }

    [Fact]
    public async Task A_PIN_stays_on_the_device_it_was_set_on()
    {
        _desktop.Locks.SetPin(_robin.Id, "482193");
        await _desktop.Sync.SyncAsync();

        var robinsPhone = await JoinAs(_robin, "robin-phone");

        Assert.False(robinsPhone.Locks.HasPin(_robin.Id));
    }

    [Fact]
    public async Task Syncing_with_nothing_new_sends_nothing()
    {
        Add(_desktop, "Book dentist");
        await _desktop.Sync.SyncAsync();

        var again = await _desktop.Sync.SyncAsync();

        Assert.Equal(0, again.Sent);
        Assert.Equal(0, again.Received);
    }

    [Fact]
    public async Task Someone_added_later_can_be_invited_too()
    {
        var sam = _desktop.Households.AddMember(_home.Id, "Sam");
        _desktop.Sync.AddPerson(sam.Id);
        Add(_desktop, "Sam's homework", owner: sam);
        await _desktop.Sync.SyncAsync();

        var samsTablet = await JoinAs(sam, "sam-tablet");

        Assert.Equal(["Sam's homework"], TitlesFor(samsTablet, sam));
    }

    [Fact]
    public void Sync_is_off_until_it_is_turned_on()
    {
        Assert.False(NewDevice("fresh").Sync.IsEnabled);
        Assert.True(_desktop.Sync.IsEnabled);
    }

    /// <summary>One device: its own encrypted database, repositories and sync engine.</summary>
    private sealed class Device : IDisposable
    {
        private readonly string _directory;
        private readonly SqliteConnection _db;

        public Device(string name, HttpSyncServer server)
        {
            _directory = System.IO.Directory.CreateTempSubdirectory($"etchasketch-{name}-").FullName;
            _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
            Schema.Apply(_db);
            Households = new HouseholdRepository(_db);
            Tasks = new TaskRepository(_db);
            Notifications = new NotificationRepository(_db);
            Reminders = new Reminders(Tasks, Notifications);
            Sync = new SyncEngine(_db, server, Clock);
            Locks = new ProfileLocks(_db, iterations: 1_000);
        }

        public ManualClock Clock { get; } = new(Wednesday);

        public HouseholdRepository Households { get; }

        public TaskRepository Tasks { get; }

        public NotificationRepository Notifications { get; }

        public Reminders Reminders { get; }

        public SyncEngine Sync { get; }

        public ProfileLocks Locks { get; }

        /// <summary>Whether any table on this device holds the text at all.</summary>
        public bool Contains(string text)
        {
            foreach (var table in new[] { "households", "profiles", "tasks", "task_completions", "notifications", "quiet_hours", "task_links", "task_comments" })
            {
                using var command = _db.CreateCommand();
                command.CommandText = $"SELECT * FROM {table}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.GetValue(i) is string value && value.Contains(text, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public void Dispose()
        {
            _db.Dispose();
            System.IO.Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task The_due_time_its_reminder_and_the_alarm_travel_with_the_task()
    {
        _desktop.Tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Vet", Due = Today.AddDays(1), DueTime = new TimeOnly(14, 5),
            RemindMinutesBefore = 30, Alarm = true,
        });
        await _desktop.Sync.SyncAsync();

        var phone = await JoinAs(_robin, "robin-phone");

        var task = phone.Tasks.VisibleTo(_robin.Id).Single();
        Assert.Equal(new TimeOnly(14, 5), task.DueTime);
        Assert.Equal(30, task.RemindMinutesBefore);
        Assert.True(task.Alarm);
    }

    [Fact]
    public async Task Subtasks_links_and_comments_reach_who_may_see_them_and_no_one_else()
    {
        var move = Add(_desktop, "Move house", PrivacyTier.Household);
        var van = _desktop.Tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Book van", Tier = PrivacyTier.Household, ParentId = move.Id,
        });
        var surprise = Add(_desktop, "Surprise for Kai");
        _desktop.Tasks.AddLink(new TaskLink(van.Id, move.Id, TaskLinkKind.Blocks), Wednesday);
        _desktop.Tasks.AddLink(new TaskLink(surprise.Id, move.Id, TaskLinkKind.Related), Wednesday);
        _desktop.Tasks.AddComment(move.Id, _robin.Id, "Saturday works", Wednesday);
        _desktop.Tasks.AddComment(surprise.Id, _robin.Id, "the bakery on 5th", Wednesday);
        await _desktop.Sync.SyncAsync();

        var kaisPhone = await JoinAs(_kai, "kai-phone");
        var robinsPhone = await JoinAs(_robin, "robin-phone");

        Assert.Equal(move.Id, kaisPhone.Tasks.Get(van.Id)!.ParentId);
        Assert.Equal([("Blocked by", "Book van")], kaisPhone.Tasks.LinksOf(move.Id, _kai.Id).Select(link => (link.Label, link.Other.Title)));
        Assert.Equal("Saturday works", Assert.Single(kaisPhone.Tasks.CommentsOf(move.Id, _kai.Id)).Text);
        Assert.False(kaisPhone.Contains("the bakery on 5th"));
        Assert.False(kaisPhone.Contains(surprise.Id.ToString()));

        Assert.Equal(2, robinsPhone.Tasks.LinksOf(move.Id, _robin.Id).Count);
        Assert.Equal("the bakery on 5th", Assert.Single(robinsPhone.Tasks.CommentsOf(surprise.Id, _robin.Id)).Text);
    }
}
