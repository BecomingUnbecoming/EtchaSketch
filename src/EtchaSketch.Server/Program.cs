using EtchaSketch.Server;

var builder = WebApplication.CreateBuilder(args);

var limits = builder.Configuration.GetSection("Sync").Get<SyncLimits>() ?? new SyncLimits();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBytes);
builder.Services.AddSyncRateLimits(limits);

// The store holds only sealed changes, so it needs no key of its own.
var storePath = builder.Configuration["Sync:StorePath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "sync.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
builder.Services.AddSingleton(new SyncStore(storePath, TimeProvider.System, limits));

var app = builder.Build();

app.UseRateLimiter();

// Sync server and relay. Phase 1 adds household sync; Phase 5 adds the relay.
app.MapSyncApi();

app.Run();
