using System.Globalization;
using EtchaSketch.Data.Sync;
using EtchaSketch.Tests.Sync;
using EtchaSketch.Tests.TestSupport;
using EtchaSketch.UI;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.Tests.Screens;

/// <summary>Turning sync on, pairing a new device and joining a household, through the screens.</summary>
public sealed class SyncScreenTests : IAsyncLifetime
{
    private const string Address = "http://sync.home.test:5080";

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-sync-screens-").FullName;
    private readonly List<MainViewModel> _apps = [];
    private readonly Dictionary<string, InMemoryKeyStore> _keys = [];
    private SyncServerFixture _server = null!;

    public SyncScreenTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public async Task InitializeAsync() => _server = await SyncServerFixture.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }

        await _server.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private MainViewModel Launch(string device, Func<Uri, ISyncServer>? server = null)
    {
        var app = new MainViewModel(new AppEnvironment(
            Path.Combine(_directory, device, "etchasketch.db"),
            _keys.TryGetValue(device, out var keys) ? keys : _keys[device] = new InMemoryKeyStore(),
            new FixedClock(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero)),
            server ?? (_ => _server.Client())));
        _apps.Add(app);
        return app;
    }

    private static HomeViewModel SetUp(MainViewModel app)
    {
        var setup = Assert.IsType<SetupViewModel>(app.CurrentPage);
        setup.YourName = "Lacy";
        setup.OtherName = "Jordy";
        setup.CreateCommand.Execute(null);
        return Assert.IsType<HomeViewModel>(app.CurrentPage);
    }

    private static void AddChore(HomeViewModel home, string title)
    {
        home.NewTaskCommand.Execute(null);
        home.Editor!.Title = title;
        home.Editor.IsShared = true;
        home.Editor.SaveCommand.Execute(null);
    }

    private static async Task<SyncViewModel> TurnOnSync(HomeViewModel home)
    {
        home.OpenSyncCommand.Execute(null);
        var sync = home.Sync!;
        sync.ServerAddress = Address;
        await sync.TurnOnCommand.ExecuteAsync(null);
        Assert.Null(sync.ErrorMessage);
        return sync;
    }

    private static async Task<string> Invite(SyncViewModel sync, string name)
    {
        await sync.Invites.Single(invite => invite.Name == name).InviteCommand.ExecuteAsync(null);
        Assert.Null(sync.ErrorMessage);
        return sync.PairingCode!;
    }

    private static async Task<SetupViewModel> TryJoin(MainViewModel app, string code, string address = Address)
    {
        var setup = Assert.IsType<SetupViewModel>(app.CurrentPage);
        setup.JoinAddress = address;
        setup.JoinCode = code;
        await setup.JoinCommand.ExecuteAsync(null);
        return setup;
    }

    [Fact]
    public void Sync_is_off_until_you_turn_it_on()
    {
        var home = SetUp(Launch("desktop"));
        home.OpenSyncCommand.Execute(null);

        Assert.False(home.Sync!.IsEnabled);
        Assert.Equal("Sync is off. Your data stays on this device.", home.Sync.StatusText);
        Assert.Empty(home.Sync.Invites);
    }

    [Fact]
    public async Task Turning_sync_on_says_when_it_last_synced_and_offers_invites()
    {
        var sync = await TurnOnSync(SetUp(Launch("desktop")));

        Assert.True(sync.IsEnabled);
        Assert.Equal("Synced at 09:00", sync.StatusText);
        Assert.Equal(["Lacy", "Jordy"], sync.Invites.Select(invite => invite.Name));
    }

    [Fact]
    public async Task Inviting_someone_shows_a_code_for_their_device()
    {
        var sync = await TurnOnSync(SetUp(Launch("desktop")));

        var code = await Invite(sync, "Jordy");

        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", code);
        Assert.Equal("Enter this code on Jordy's device within 15 minutes. It works once.", sync.PairingHint);
    }

    [Fact]
    public async Task A_new_device_joins_with_the_code_and_opens_on_that_persons_home()
    {
        var desktop = SetUp(Launch("desktop"));
        AddChore(desktop, "Dishes");
        var code = await Invite(await TurnOnSync(desktop), "Jordy");

        var phone = Launch("jordy-phone");
        var setup = await TryJoin(phone, code);

        Assert.Null(setup.JoinErrorMessage);
        var home = Assert.IsType<HomeViewModel>(phone.CurrentPage);
        Assert.Equal("Hi, Jordy", home.Greeting);
        Assert.Equal(["Dishes"], home.MyTasks.Select(task => task.Title));
    }

    [Fact]
    public async Task Changes_arrive_on_the_next_sync()
    {
        var desktop = SetUp(Launch("desktop"));
        var sync = await TurnOnSync(desktop);
        var code = await Invite(sync, "Jordy");
        var phone = Launch("jordy-phone");
        await TryJoin(phone, code);
        var jordy = Assert.IsType<HomeViewModel>(phone.CurrentPage);

        AddChore(desktop, "Take out bins");
        await desktop.AutoSyncCommand.ExecuteAsync(null);
        await jordy.AutoSyncCommand.ExecuteAsync(null);

        Assert.Equal(["Take out bins"], jordy.MyTasks.Select(task => task.Title));
    }

    [Fact]
    public async Task Sync_carries_on_after_a_restart()
    {
        var desktop = SetUp(Launch("desktop"));
        await TurnOnSync(desktop);

        var again = Assert.IsType<HomeViewModel>(Launch("desktop").CurrentPage);
        again.OpenSyncCommand.Execute(null);

        Assert.True(again.Sync!.IsEnabled);
        Assert.Equal(Address + "/", again.Sync.ServerAddress);
    }

    [Fact]
    public async Task A_used_code_is_explained()
    {
        var desktop = SetUp(Launch("desktop"));
        var code = await Invite(await TurnOnSync(desktop), "Jordy");
        await TryJoin(Launch("jordy-phone"), code);

        var setup = await TryJoin(Launch("someone-else"), code);

        Assert.Equal(new PairingCodeNotFoundException().Message, setup.JoinErrorMessage);
        Assert.IsType<SetupViewModel>(_apps[^1].CurrentPage);
    }

    [Fact]
    public async Task A_mistyped_code_is_explained()
    {
        var setup = await TryJoin(Launch("phone"), "ABCD-EFGH");

        Assert.Equal("That isn't a whole pairing code.", setup.JoinErrorMessage);
    }

    [Fact]
    public async Task A_bad_address_is_explained()
    {
        var home = SetUp(Launch("desktop"));
        home.OpenSyncCommand.Execute(null);
        home.Sync!.ServerAddress = "not an address";

        await home.Sync.TurnOnCommand.ExecuteAsync(null);

        Assert.Equal("Enter the sync server's address, like http://192.168.1.20:5080.", home.Sync.ErrorMessage);
        Assert.False(home.Sync.IsEnabled);
    }

    [Fact]
    public async Task An_unreachable_server_is_explained()
    {
        var home = SetUp(Launch("desktop", _ => new HttpSyncServer(new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri(Address) })));
        home.OpenSyncCommand.Execute(null);
        home.Sync!.ServerAddress = Address;

        await home.Sync.TurnOnCommand.ExecuteAsync(null);

        Assert.Equal($"Couldn't reach the sync server at {Address}/. Check the address, and that this device is on your home network or VPN.",
            home.Sync.ErrorMessage);
    }

    [Fact]
    public async Task Someone_added_later_can_be_invited()
    {
        var app = Launch("desktop");
        var home = SetUp(app);
        await TurnOnSync(home);
        home.Sync!.CloseCommand.Execute(null);
        home.SwitchPersonCommand.Execute(null);
        var picker = Assert.IsType<ProfilePickerViewModel>(app.CurrentPage);
        picker.NewPersonName = "Sam";
        picker.AddPersonCommand.Execute(null);
        picker.People.Single(person => person.Name == "Lacy").ChooseCommand.Execute(null);

        var lacy = Assert.IsType<HomeViewModel>(app.CurrentPage);
        lacy.OpenSyncCommand.Execute(null);

        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", await Invite(lacy.Sync!, "Sam"));
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host");
    }
}
