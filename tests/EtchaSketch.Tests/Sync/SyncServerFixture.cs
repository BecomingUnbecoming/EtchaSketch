using EtchaSketch.Data.Sync;
using EtchaSketch.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EtchaSketch.Tests.Sync;

/// <summary>A real sync server running in memory, with its store in a temporary folder.</summary>
internal sealed class SyncServerFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SyncServerFixture(WebApplication app, string directory, SyncStore store)
    {
        _app = app;
        Directory = directory;
        Store = store;
    }

    public string Directory { get; }

    public SyncStore Store { get; }

    public string StorePath => Path.Combine(Directory, "sync.db");

    public static async Task<SyncServerFixture> StartAsync(TimeProvider? clock = null, SyncLimits? limits = null, string? joinToken = null)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("etchasketch-server-").FullName;
        var store = new SyncStore(Path.Combine(directory, "sync.db"), clock ?? TimeProvider.System, limits);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new SyncAccess(joinToken));
        builder.Services.AddSyncRateLimits(limits ?? new SyncLimits());
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapSyncApi();
        await app.StartAsync();
        return new SyncServerFixture(app, directory, store);
    }

    public HttpClient HttpClient() => _app.GetTestClient();

    public HttpSyncServer Client() => new(HttpClient());

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        Store.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}
