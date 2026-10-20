using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Sync;
using EtchaSketch.Data.Tasks;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Assistant;

/// <summary>
/// The assistant's own encrypted copy of the household's shared data. It joined with a
/// household-only code, so it never holds anyone's private key: private tasks can't reach it.
/// One request or sync at a time, since they share one database connection.
/// </summary>
public sealed class AssistantData : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _db;
    private readonly AssistantOptions _options;

    private AssistantData(SqliteConnection db, AssistantOptions options, ISyncServer server, TimeProvider clock)
    {
        _db = db;
        _options = options;
        Clock = clock;
        Households = new HouseholdRepository(db);
        Tasks = new TaskRepository(db);
        Sync = new SyncEngine(db, server, clock) { DeviceName = options.DeviceName };
        if (!string.IsNullOrWhiteSpace(options.JoinToken))
        {
            Sync.SetJoinToken(options.JoinToken);
        }
    }

    public TimeProvider Clock { get; }

    public HouseholdRepository Households { get; }

    public TaskRepository Tasks { get; }

    public SyncEngine Sync { get; }

    /// <summary>The household it belongs to, or null before it has joined one.</summary>
    public Household? Household => Households.All() is [var only] ? only : null;

    public static AssistantData Open(AssistantOptions options, IKeyStore keys, ISyncServer server, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var db = EncryptedDatabase.Open(options.DatabasePath, keys);
        try
        {
            Schema.Apply(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return new AssistantData(db, options, server, clock ?? TimeProvider.System);
    }

    /// <summary>The person new tasks, completions and comments are made as, or null if none is set or found.</summary>
    public Profile? Actor() => Household?.Members.FirstOrDefault(member =>
        string.Equals(member.DisplayName, _options.ActAs?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs one piece of work with the database to itself.</summary>
    public async Task<T> UseAsync<T>(Func<AssistantData, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            return await work(this);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<T> Use<T>(Func<AssistantData, T> work) => UseAsync(data => Task.FromResult(work(data)));

    public void Dispose()
    {
        _db.Dispose();
        _gate.Dispose();
    }
}
