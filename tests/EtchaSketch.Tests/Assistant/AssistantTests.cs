using System.Net;
using System.Net.Http.Json;
using EtchaSketch.Assistant;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Sync;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Sync;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.Sync;
using EtchaSketch.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtchaSketch.Tests.Assistant;

/// <summary>The household assistant, joined to a real household through a real sync server.</summary>
public sealed class AssistantTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-assistant-").FullName;
    private SyncServerFixture _server = null!;
    private SqliteConnection _desktopDb = null!;
    private TaskRepository _desktopTasks = null!;
    private SyncEngine _desktopSync = null!;
    private Household _home = null!;
    private Profile _robin = null!;
    private Profile _kai = null!;
    private AssistantData _assistant = null!;
    private Callers _callers = null!;
    private WebApplication _api = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _server = await SyncServerFixture.StartAsync();
        Directory.CreateDirectory(Path.Combine(_directory, "desktop"));
        _desktopDb = EncryptedDatabase.Open(Path.Combine(_directory, "desktop", "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_desktopDb);
        var households = new HouseholdRepository(_desktopDb);
        var created = households.Create("Home", "Robin");
        _robin = created.Members[0];
        _kai = households.AddMember(created.Id, "Kai");
        _home = households.Get(created.Id)!;
        households.SetCurrentProfile(_robin.Id);
        _desktopTasks = new TaskRepository(_desktopDb);
        _desktopSync = new SyncEngine(_desktopDb, _server.Client()) { DeviceName = "desktop" };
        _desktopSync.Enable(_home.Id);

        _desktopTasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Dishes", Tier = PrivacyTier.Household, AssigneeId = _kai.Id });
        _desktopTasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Surprise for Kai", Notes = "the bakery on 5th" });
        var code = await _desktopSync.InviteAssistantAsync(_home.Id);

        var options = new AssistantOptions
        {
            DataDirectory = Path.Combine(_directory, "assistant", "data"),
            KeyDirectory = Path.Combine(_directory, "assistant", "keys"),
            ActAs = "robin",
        };
        _assistant = AssistantData.Open(options, new FileKeyStore(options.KeyDirectory), _server.Client());
        await _assistant.Sync.JoinAsync(code);

        _callers = new Callers(options.CallersPath);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_assistant);
        _api = builder.Build();
        _api.MapAssistantApi(_callers);
        await _api.StartAsync();
        _http = _api.GetTestClient();
        _http.DefaultRequestHeaders.Authorization = new("Bearer", _callers.Add("home-assistant"));
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        _assistant.Dispose();
        _desktopDb.Dispose();
        await _server.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<List<AssistantTask>> Tasks(string query = "") =>
        (await _http.GetFromJsonAsync<List<AssistantTask>>("/v1/tasks" + query))!;

    [Fact]
    public async Task It_holds_and_shows_shared_tasks_and_never_private_ones()
    {
        var tasks = await Tasks();

        var dishes = Assert.Single(tasks);
        Assert.Equal(("Dishes", "Kai"), (dishes.Title, dishes.AssignedTo));
        // It has no private key, so even asking as Robin finds none of her private tasks in its copy.
        var privateRows = await _assistant.Use(d =>
            d.Tasks.ExportableBy(_robin.Id).Concat(d.Tasks.ExportableBy(_kai.Id)).Count(task => task.Tier > PrivacyTier.Household));
        Assert.Equal(0, privateRows);
        Assert.Equal(["Dishes"], (await Tasks("?assignedTo=kai")).Select(task => task.Title));
        Assert.Empty(await Tasks("?assignedTo=robin"));
    }

    [Fact]
    public async Task A_task_added_through_it_reaches_the_household_as_a_shared_task()
    {
        var response = await _http.PostAsJsonAsync("/v1/tasks", new NewTask("Buy milk", Due: "2026-10-22", DueTime: "18:00", Labels: ["shopping"], AssignTo: "Kai"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await _assistant.UseAsync(d => d.Sync.SyncAsync());
        await _desktopSync.SyncAsync();

        var milk = _desktopTasks.VisibleTo(_kai.Id).Single(task => task.Title == "Buy milk");
        Assert.Equal((PrivacyTier.Household, _robin.Id, _kai.Id), (milk.Tier, milk.OwnerId, milk.AssigneeId));
        Assert.Equal(new TimeOnly(18, 0), milk.DueTime);

        var bad = await _http.PostAsJsonAsync("/v1/tasks", new NewTask("Typo", AssignTo: "Nobody"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Finishing_through_it_waits_for_blockers_and_reaches_the_household()
    {
        var dishes = Assert.Single(await Tasks());
        var created = await (await _http.PostAsJsonAsync("/v1/tasks", new NewTask("Run the dishwasher"))).Content.ReadFromJsonAsync<AssistantTask>();
        await _assistant.Use(d => d.Tasks.AddLink(new TaskLink(created!.Id, dishes.Id, TaskLinkKind.Blocks), d.Clock.GetUtcNow()));

        var blocked = await _http.PostAsync($"/v1/tasks/{dishes.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Run the dishwasher", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsync($"/v1/tasks/{created!.Id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsync($"/v1/tasks/{dishes.Id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync($"/v1/tasks/{Guid.NewGuid()}/complete", null)).StatusCode);

        await _assistant.UseAsync(d => d.Sync.SyncAsync());
        await _desktopSync.SyncAsync();
        Assert.DoesNotContain(_desktopTasks.VisibleTo(_robin.Id), task => task.Title is "Dishes" or "Run the dishwasher");
    }

    [Fact]
    public async Task Comments_added_through_it_reach_the_household()
    {
        var dishes = Assert.Single(await Tasks());

        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsJsonAsync($"/v1/tasks/{dishes.Id}/comments", new NewComment("Dishwasher's full"))).StatusCode);
        await _assistant.UseAsync(d => d.Sync.SyncAsync());
        await _desktopSync.SyncAsync();

        Assert.Equal("Dishwasher's full", Assert.Single(_desktopTasks.CommentsOf(dishes.Id, _robin.Id)).Text);
    }

    [Fact]
    public async Task Every_caller_needs_its_own_token_and_can_be_cut_off_alone()
    {
        var openWebUi = _callers.Add("open-webui");
        using var anonymous = _api.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/tasks")).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", openWebUi);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/v1/tasks")).StatusCode);

        _callers.Remove("open-webui");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/v1/tasks")).StatusCode);
    }

    [Fact]
    public async Task Removing_it_from_the_household_cuts_it_off_like_a_lost_phone()
    {
        await _desktopSync.SyncAsync();
        var listed = Assert.Single(_desktopSync.Devices(_home.Id), device => !device.IsThisDevice);
        Assert.Equal(("Household assistant", null), (listed.Name, listed.ProfileId));

        await _desktopSync.RemoveDeviceAsync(listed.Id);

        await Assert.ThrowsAsync<SyncAccessDeniedException>(() => _assistant.UseAsync(d => d.Sync.SyncAsync()));
    }
}
