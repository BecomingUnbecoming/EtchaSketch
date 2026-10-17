using System.Globalization;
using EtchaSketch.Tests.TestSupport;
using EtchaSketch.UI;
using EtchaSketch.UI.Services;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.Tests.Screens;

/// <summary>Setting a PIN, unlocking a profile, and the app locking itself, through the screens.</summary>
public sealed class LockScreenTests : IDisposable
{
    private const string Pin = "4821";

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-lock-screens-").FullName;
    private readonly InMemoryKeyStore _keys = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly List<MainViewModel> _apps = [];

    public LockScreenTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    private MainViewModel Launch(IDeviceUnlock? deviceUnlock = null)
    {
        var app = new MainViewModel(new AppEnvironment(Path.Combine(_directory, "etchasketch.db"), _keys, _clock,
            DeviceUnlock: deviceUnlock));
        _apps.Add(app);
        return app;
    }

    private static HomeViewModel SetUp(MainViewModel app)
    {
        var setup = Assert.IsType<SetupViewModel>(app.CurrentPage);
        setup.YourName = "Robin";
        setup.OtherName = "Kai";
        setup.CreateCommand.Execute(null);
        return Assert.IsType<HomeViewModel>(app.CurrentPage);
    }

    private static LockSettingsViewModel SetPin(HomeViewModel home, string pin = Pin)
    {
        home.OpenLockSettingsCommand.Execute(null);
        var settings = home.LockSettings!;
        settings.NewPin = pin;
        settings.ConfirmPin = pin;
        settings.SavePinCommand.Execute(null);
        Assert.Null(settings.ErrorMessage);
        return settings;
    }

    private static UnlockViewModel Locked(MainViewModel app) => Assert.IsType<UnlockViewModel>(app.CurrentPage);

    private static HomeViewModel Unlock(MainViewModel app, string pin = Pin)
    {
        var unlock = Locked(app);
        unlock.Pin = pin;
        unlock.UnlockCommand.Execute(null);
        return Assert.IsType<HomeViewModel>(app.CurrentPage);
    }

    [Fact]
    public void Without_a_PIN_the_app_opens_straight_to_your_home_screen()
    {
        var home = SetUp(Launch());

        Assert.False(home.CanLock);
        Assert.IsType<HomeViewModel>(Launch().CurrentPage);
    }

    [Fact]
    public void Once_you_set_a_PIN_the_app_asks_for_it_when_it_opens()
    {
        var home = SetUp(Launch());
        SetPin(home);

        var unlock = Locked(Launch());

        Assert.Equal("Robin", unlock.Name);
        Assert.Equal("Hi, Robin", Unlock(_apps[^1]).Greeting);
    }

    [Fact]
    public void A_wrong_PIN_keeps_the_profile_locked()
    {
        SetPin(SetUp(Launch()));
        var app = Launch();
        var unlock = Locked(app);

        unlock.Pin = "1111";
        unlock.UnlockCommand.Execute(null);

        Assert.Same(unlock, app.CurrentPage);
        Assert.Equal("That PIN isn't right. 4 more tries before a short wait.", unlock.ErrorMessage);
        Assert.Equal("", unlock.Pin);
    }

    [Fact]
    public void Too_many_wrong_PINs_mean_a_wait()
    {
        SetPin(SetUp(Launch()));
        var app = Launch();
        var unlock = Locked(app);

        for (var i = 0; i < 5; i++)
        {
            unlock.Pin = "1111";
            unlock.UnlockCommand.Execute(null);
        }

        Assert.Equal("Too many wrong tries. Try again in 30 seconds.", unlock.ErrorMessage);

        unlock.Pin = Pin;
        unlock.UnlockCommand.Execute(null);
        Assert.Same(unlock, app.CurrentPage);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Unlock(app);
    }

    [Fact]
    public void Locking_from_the_home_screen_asks_for_the_PIN_again()
    {
        var app = Launch();
        var home = SetUp(app);
        SetPin(home);
        home.LockSettings!.CloseCommand.Execute(null);

        Assert.True(home.CanLock);
        home.LockCommand.Execute(null);

        Locked(app);
    }

    [Fact]
    public void Someone_else_can_switch_person_from_the_lock_screen()
    {
        var app = Launch();
        SetPin(SetUp(app));
        app.LockNow();

        Locked(app).SwitchPersonCommand.Execute(null);

        var picker = Assert.IsType<ProfilePickerViewModel>(app.CurrentPage);
        Assert.Equal([("Robin", true), ("Kai", false)], picker.People.Select(person => (person.Name, person.IsLocked)));

        picker.People.Single(person => person.Name == "Kai").ChooseCommand.Execute(null);
        Assert.Equal("Hi, Kai", Assert.IsType<HomeViewModel>(app.CurrentPage).Greeting);
    }

    [Fact]
    public void Choosing_a_locked_person_in_the_picker_asks_for_their_PIN()
    {
        var app = Launch();
        var home = SetUp(app);
        SetPin(home);
        home.SwitchPersonCommand.Execute(null);

        Assert.IsType<ProfilePickerViewModel>(app.CurrentPage).People.Single(person => person.Name == "Robin").ChooseCommand.Execute(null);

        Assert.Equal("Hi, Robin", Unlock(app).Greeting);
    }

    [Fact]
    public void Five_minutes_without_use_locks_the_app()
    {
        var app = Launch();
        SetPin(SetUp(app));

        _clock.Advance(TimeSpan.FromMinutes(4));
        app.NoteActivity();
        _clock.Advance(TimeSpan.FromMinutes(4));
        app.LockIfIdle();
        Assert.IsType<HomeViewModel>(app.CurrentPage);

        _clock.Advance(TimeSpan.FromMinutes(1));
        app.LockIfIdle();
        Locked(app);
    }

    [Fact]
    public void A_profile_without_a_PIN_never_locks_itself()
    {
        var app = Launch();
        SetUp(app);

        _clock.Advance(TimeSpan.FromHours(1));
        app.LockIfIdle();
        app.LockNow();

        Assert.IsType<HomeViewModel>(app.CurrentPage);
    }

    [Fact]
    public void Leaving_the_app_for_more_than_a_minute_locks_it()
    {
        var app = Launch();
        SetPin(SetUp(app));

        app.EnteredBackground();
        _clock.Advance(TimeSpan.FromSeconds(50));
        app.ReturnedToForeground();
        Assert.IsType<HomeViewModel>(app.CurrentPage);

        app.EnteredBackground();
        _clock.Advance(TimeSpan.FromSeconds(61));
        app.ReturnedToForeground();
        Locked(app);
    }

    [Fact]
    public void The_two_new_PINs_must_match()
    {
        var home = SetUp(Launch());
        home.OpenLockSettingsCommand.Execute(null);
        var settings = home.LockSettings!;

        settings.NewPin = "4821";
        settings.ConfirmPin = "4812";
        settings.SavePinCommand.Execute(null);

        Assert.Equal("The two PINs don't match.", settings.ErrorMessage);
        Assert.False(settings.HasPin);
    }

    [Fact]
    public void A_short_PIN_is_explained()
    {
        var home = SetUp(Launch());
        home.OpenLockSettingsCommand.Execute(null);
        var settings = home.LockSettings!;

        settings.NewPin = "12";
        settings.ConfirmPin = "12";
        settings.SavePinCommand.Execute(null);

        Assert.Equal("A PIN is 4 to 12 digits.", settings.ErrorMessage);
    }

    [Fact]
    public void Changing_or_removing_the_PIN_needs_the_current_one()
    {
        var app = Launch();
        var home = SetUp(app);
        var settings = SetPin(home);

        settings.CurrentPin = "1111";
        settings.NewPin = "9999";
        settings.ConfirmPin = "9999";
        settings.SavePinCommand.Execute(null);
        Assert.Equal("Your current PIN isn't right.", settings.ErrorMessage);

        settings.CurrentPin = "1111";
        settings.RemovePinCommand.Execute(null);
        Assert.Equal("Your current PIN isn't right.", settings.ErrorMessage);
        Assert.True(settings.HasPin);

        settings.CurrentPin = Pin;
        settings.NewPin = "9999";
        settings.ConfirmPin = "9999";
        settings.SavePinCommand.Execute(null);
        Assert.Null(settings.ErrorMessage);
        app.LockNow();
        Unlock(app, "9999");

        home = Assert.IsType<HomeViewModel>(app.CurrentPage);
        home.OpenLockSettingsCommand.Execute(null);
        settings = home.LockSettings!;
        settings.CurrentPin = "9999";
        settings.RemovePinCommand.Execute(null);
        Assert.False(settings.HasPin);
        Assert.IsType<HomeViewModel>(Launch().CurrentPage);
    }

    [Fact]
    public void Device_unlock_is_offered_only_where_the_device_has_one()
    {
        var home = SetUp(Launch());
        Assert.False(SetPin(home).CanUseDeviceUnlock);

        var phone = new FakeDeviceUnlock();
        home = Assert.IsType<HomeViewModel>(Unlock(Launch(phone)));
        home.OpenLockSettingsCommand.Execute(null);
        Assert.True(home.LockSettings!.CanUseDeviceUnlock);
    }

    [Fact]
    public void Device_unlock_opens_the_profile_when_the_device_confirms_it_is_you()
    {
        var phone = new FakeDeviceUnlock();
        var app = Launch(phone);
        var settings = SetPin(SetUp(app));
        settings.UseDeviceUnlock = true;
        app.LockNow();

        var unlock = Locked(app);
        Assert.True(unlock.CanUseDeviceUnlock);

        phone.Confirms = false;
        unlock.DeviceUnlockCommand.Execute(null);
        Assert.Same(unlock, app.CurrentPage);

        phone.Confirms = true;
        unlock.DeviceUnlockCommand.Execute(null);
        Assert.IsType<HomeViewModel>(app.CurrentPage);
        Assert.Equal("Unlock Robin's profile", phone.LastReason);
    }

    [Fact]
    public void Device_unlock_stays_off_until_you_turn_it_on()
    {
        var app = Launch(new FakeDeviceUnlock());
        SetPin(SetUp(app));
        app.LockNow();

        Assert.False(Locked(app).CanUseDeviceUnlock);
    }

    private sealed class FakeDeviceUnlock : IDeviceUnlock
    {
        public bool Confirms { get; set; } = true;

        public string? LastReason { get; private set; }

        public bool IsAvailable => true;

        public Task<bool> ConfirmAsync(string reason, CancellationToken cancellationToken = default)
        {
            LastReason = reason;
            return Task.FromResult(Confirms);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
