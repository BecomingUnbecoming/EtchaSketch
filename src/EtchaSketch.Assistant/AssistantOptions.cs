namespace EtchaSketch.Assistant;

/// <summary>
/// How the assistant is set up, from the "Assistant" configuration section (environment variables
/// like <c>Assistant__SyncServer</c>). See docs/assistant.md.
/// </summary>
public sealed record AssistantOptions
{
    /// <summary>Where the assistant keeps its encrypted copy of the household's shared data and its callers.</summary>
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Where the database key lives. Keep it on different storage from the data (a separate volume),
    /// so a copy of the data alone can't be read.
    /// </summary>
    public string KeyDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "keys");

    /// <summary>The household's sync server, like http://192.168.1.20:5080.</summary>
    public string? SyncServer { get; init; }

    /// <summary>The sync server's join token, if it has one. A pairing code carries it too.</summary>
    public string? JoinToken { get; init; }

    /// <summary>The person new tasks, completions and comments are made as, by their name in the household.</summary>
    public string? ActAs { get; init; }

    /// <summary>What the assistant is called in the household's device list.</summary>
    public string DeviceName { get; init; } = "Household assistant";

    /// <summary>How often it syncs, in seconds.</summary>
    public int SyncSeconds { get; init; } = 60;

    public string DatabasePath => Path.Combine(DataDirectory, "assistant.db");

    public string CallersPath => Path.Combine(DataDirectory, "callers.json");
}
