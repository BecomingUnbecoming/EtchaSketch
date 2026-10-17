using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EtchaSketch.Data.Notifications;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Sync;
using EtchaSketch.Data.Tasks;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.UI.Services;

/// <summary>The open, unlocked database and everything the screens read and write through it.</summary>
public sealed class AppSession : IDisposable
{
    private const string SyncServerSetting = "sync_server";

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly Func<Uri, ISyncServer> _syncServers;
    private readonly DeviceSettings _settings;
    private bool _syncing;

    private AppSession(SqliteConnection connection, TimeProvider clock, Func<Uri, ISyncServer> syncServers, IDeviceUnlock? deviceUnlock)
    {
        _connection = connection;
        DeviceUnlock = deviceUnlock;
        _clock = clock;
        _syncServers = syncServers;
        _settings = new DeviceSettings(connection);
        Households = new HouseholdRepository(connection);
        Tasks = new TaskRepository(connection);
        Notifications = new NotificationRepository(connection);
        Reminders = new Reminders(Tasks, Notifications);
        Locks = new ProfileLocks(connection);

        if (_settings.Get(SyncServerSetting) is { } address && Uri.TryCreate(address, UriKind.Absolute, out var server))
        {
            Connect(server);
        }
    }

    public HouseholdRepository Households { get; }

    public TaskRepository Tasks { get; }

    public NotificationRepository Notifications { get; }

    public Reminders Reminders { get; }

    /// <summary>Each person's PIN on this device.</summary>
    public ProfileLocks Locks { get; }

    /// <summary>The device's fingerprint or screen lock, or null where the platform has none.</summary>
    public IDeviceUnlock? DeviceUnlock { get; }

    public bool HasDeviceUnlock => DeviceUnlock?.IsAvailable == true;

    /// <summary>The sync server this device uses, or null before one is set.</summary>
    public Uri? SyncServerAddress { get; private set; }

    /// <summary>Sync with <see cref="SyncServerAddress"/>, or null before one is set.</summary>
    public SyncEngine? Sync { get; private set; }

    public bool IsSyncOn => Sync?.IsEnabled == true;

    /// <summary>When this device last finished a sync since the app started.</summary>
    public DateTimeOffset? LastSynced { get; private set; }

    public DateTimeOffset Now => _clock.GetLocalNow();

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>A stored time as the clock on this device shows it.</summary>
    public DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, _clock.LocalTimeZone);

    public static AppSession Open(AppEnvironment environment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(environment.DatabasePath))!);
        var connection = EncryptedDatabase.Open(environment.DatabasePath, environment.KeyStore);
        try
        {
            Schema.Apply(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new AppSession(connection, environment.Clock ?? TimeProvider.System,
            environment.SyncServerFactory ?? (address => new HttpSyncServer(new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) })),
            environment.DeviceUnlock);
    }

    /// <summary>Starts syncing a household set up on this device through the server at an address.</summary>
    public async Task TurnOnSyncAsync(Uri address, Guid householdId, CancellationToken cancellationToken = default)
    {
        Connect(address).Enable(householdId);
        await SyncAsync(cancellationToken);
    }

    /// <summary>Joins a household with a pairing code from one of its devices. Returns the profile it was for.</summary>
    public async Task<Guid> JoinAsync(Uri address, string pairingCode, CancellationToken cancellationToken = default)
    {
        var profileId = await Connect(address).JoinAsync(pairingCode, cancellationToken);
        LastSynced = Now;
        return profileId;
    }

    /// <summary>
    /// Syncs if sync is on and no other sync is running. Returns what changed, or null when it
    /// didn't run.
    /// </summary>
    public async Task<SyncResult?> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSyncOn || _syncing)
        {
            return null;
        }

        _syncing = true;
        try
        {
            var result = await Sync!.SyncAsync(cancellationToken);
            LastSynced = Now;
            return result;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Brings a household back on this empty device from a recovery key. Returns the profile it was made on.</summary>
    public async Task<Guid> RestoreAsync(Uri address, string recoveryKey, string passphrase, CancellationToken cancellationToken = default)
    {
        var profileId = await Connect(address).RestoreAsync(recoveryKey, passphrase, cancellationToken);
        LastSynced = Now;
        return profileId;
    }

    /// <summary>The keys this device holds, locked under a passphrase, as text to keep somewhere safe.</summary>
    public string CreateRecoveryKey(string passphrase) => Sync!.CreateRecoveryKey(passphrase);

    private SyncEngine Connect(Uri address)
    {
        if (address != SyncServerAddress)
        {
            _settings.Set(SyncServerSetting, address.ToString());
            SyncServerAddress = address;
            Sync = new SyncEngine(_connection, _syncServers(address), _clock);
        }

        return Sync!;
    }

    public void Dispose() => _connection.Dispose();
}
