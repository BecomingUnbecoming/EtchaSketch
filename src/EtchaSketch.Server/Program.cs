using EtchaSketch.Server;

var builder = WebApplication.CreateBuilder(args);

// The store holds only sealed changes, so it needs no key of its own.
var storePath = builder.Configuration["Sync:StorePath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "sync.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
builder.Services.AddSingleton(new SyncStore(storePath, TimeProvider.System));

var app = builder.Build();

// Sync server and relay. Phase 1 adds household sync; Phase 5 adds the relay.
app.MapGet("/health", () => "ok");
app.MapSyncApi();

app.Run();
