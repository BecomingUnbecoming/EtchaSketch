using EtchaSketch.Assistant;
using EtchaSketch.Data.Sync;

// The household assistant: a copy of EtchaSketch with no screen that holds only the household's
// shared tasks and offers them to Home Assistant, Open WebUI and Claude. See docs/assistant.md.
//   join <code>           join the household with an assistant code from Sync settings
//   add-caller <name>     make a token for something that will use the API (shown once)
//   remove-caller <name>  stop accepting a caller's token
//   callers               list the callers
//   serve                 run the API (the default)

var builder = WebApplication.CreateBuilder(args.Length > 0 && args[0] != "serve" ? [] : args);
var options = builder.Configuration.GetSection("Assistant").Get<AssistantOptions>() ?? new AssistantOptions();
var callers = new Callers(options.CallersPath);

switch (args.FirstOrDefault())
{
    case "add-caller" when args.Length == 2:
        Console.WriteLine($"Token for {args[1]} (shown once, keep it in that program's settings):");
        Console.WriteLine(callers.Add(args[1]));
        return 0;
    case "remove-caller" when args.Length == 2:
        Console.WriteLine(callers.Remove(args[1]) ? $"{args[1]} can no longer use the assistant." : $"There's no caller called {args[1]}.");
        return 0;
    case "callers":
        Console.WriteLine(string.Join(Environment.NewLine, callers.Names()));
        return 0;
    case "join" when args.Length == 2:
        using (var joining = Open(options))
        {
            await joining.Sync.JoinAsync(args[1]);
            Console.WriteLine($"Joined {joining.Household?.Name}. It holds only the household's shared tasks.");
        }

        return 0;
    case null or "serve":
        break;
    default:
        Console.Error.WriteLine("Commands: join <code> · add-caller <name> · remove-caller <name> · callers · serve");
        return 2;
}

var data = Open(options);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(data);
builder.Services.AddHostedService<SyncLoop>();
var app = builder.Build();
if (data.Household is null)
{
    app.Logger.LogWarning("Not in a household yet. Make an assistant code in the app (Sync → For the assistant) and run: join <code>");
}

if (data.Actor() is null)
{
    app.Logger.LogWarning("Assistant__ActAs isn't set to someone in the household, so tasks can be read but not added, finished or commented on.");
}

app.MapAssistantApi(callers);
app.Run();
return 0;

static AssistantData Open(AssistantOptions options)
{
    if (!Uri.TryCreate(options.SyncServer, UriKind.Absolute, out var address))
    {
        throw new InvalidOperationException("Set Assistant__SyncServer to the household's sync server, like http://192.168.1.20:5080.");
    }

    return AssistantData.Open(options, new FileKeyStore(options.KeyDirectory),
        new HttpSyncServer(new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) }));
}
