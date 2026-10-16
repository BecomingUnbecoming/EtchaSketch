var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Sync server and relay. Phase 1 adds household sync; Phase 5 adds the relay.
app.MapGet("/health", () => "ok");

app.Run();
