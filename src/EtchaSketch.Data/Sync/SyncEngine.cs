using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtchaSketch.Core.Sync;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Sync;

/// <summary>What one sync sent and took in.</summary>
public sealed record SyncResult(int Sent, int Received);

/// <summary>
/// Keeps this device's data in step with the household's other devices, end to end encrypted.
/// </summary>
/// <remarks>
/// <para>
/// Every syncing row lives in one stream: the household's, for what everyone shares, or one
/// person's, for what only their devices may hold. Each stream has its own random key, and a
/// device only holds the keys it was given, so Kai's phone never receives Robin's private tasks.
/// Rows are sealed with the stream's key before they leave; the server stores bytes it can't read.
/// </para>
/// <para>
/// A sync finds what changed here since last time, takes in other devices' changes, then sends its
/// own. Each change carries a version (a clock reading plus this device's id); when two devices
/// change the same row, the higher version wins on every device.
/// </para>
/// </remarks>
public sealed class SyncEngine(SqliteConnection connection, ISyncServer server, TimeProvider? clock = null)
{
    private const int PushBatchSize = 200;
    private const int ConstraintFailed = 19;
    private const string DeviceIdKey = "sync_device_id";
    private const string ClockKey = "sync_clock";

    private const string JoinTokenKey = "sync_join_token";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DeviceSettings _settings = new(connection);
    private readonly Dictionary<string, HashSet<string>> _columns = [];

    /// <summary>
    /// The secret the sync server may ask for before it lets a device start anything. It travels to
    /// new devices with their pairing code and sits in the recovery key. Null when the server has none.
    /// </summary>
    public string? JoinToken => _settings.Get(JoinTokenKey) is { Length: > 0 } token ? token : null;

    /// <summary>Remembers the server's join token on this device. Blank clears it.</summary>
    public void SetJoinToken(string? token) => _settings.Set(JoinTokenKey, token?.Trim() ?? "");

    /// <summary>True once this device holds a household's key.</summary>
    public bool IsEnabled =>
        Scalar("SELECT 1 FROM sync_keys WHERE scope LIKE $prefix LIMIT 1", ("$prefix", SyncScopes.HouseholdPrefix + "%")) is not null;

    /// <summary>Turns sync on for a household set up on this device, making keys for it and everyone in it.</summary>
    public void Enable(Guid householdId)
    {
        var household = new HouseholdRepository(connection).Get(householdId)
                        ?? throw new ArgumentException($"There is no household {householdId}.", nameof(householdId));

        AddKey(SyncScopes.Household(household.Id));
        foreach (var member in household.Members)
        {
            AddKey(SyncScopes.Person(member.Id));
        }
    }

    /// <summary>Makes a private stream for someone added to the household on this device.</summary>
    public void AddPerson(Guid profileId)
    {
        if (IsEnabled)
        {
            AddKey(SyncScopes.Person(profileId));
        }
    }

    /// <summary>
    /// Brings the server up to date, then leaves the household's key and one person's key there,
    /// sealed under a new one-time code. Typing the code on a new device makes it that person's.
    /// </summary>
    public async Task<string> InviteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var household = new HouseholdRepository(connection).HouseholdOf(profileId)
                        ?? throw new ArgumentException($"There is no profile {profileId}.", nameof(profileId));

        var keys = new Dictionary<string, byte[]>();
        foreach (var scope in new[] { SyncScopes.Household(household.Id), SyncScopes.Person(profileId) })
        {
            keys[scope] = KeyFor(scope) ?? throw new InvalidOperationException("This device can't invite that person, because it doesn't hold their key.");
        }

        await SyncAsync(cancellationToken);

        var code = PairingCode.New();
        var passes = await EnrolmentsAsync(keys.Keys, cancellationToken);
        var bundle = JsonSerializer.SerializeToUtf8Bytes(new PairingBundle(profileId, keys, JoinToken, passes));
        await Remote().PutPairingAsync(code.PairingId, code.Seal(bundle), cancellationToken);
        return code.Text;
    }

    /// <summary>
    /// Like <see cref="InviteAsync"/>, for the household assistant: the code carries the household's
    /// key only, so the assistant can see shared tasks and never anyone's private ones.
    /// </summary>
    public async Task<string> InviteAssistantAsync(Guid householdId, CancellationToken cancellationToken = default)
    {
        var scope = SyncScopes.Household(householdId);
        var keys = new Dictionary<string, byte[]>
        {
            [scope] = KeyFor(scope) ?? throw new InvalidOperationException("Turn on sync for this household first."),
        };

        await SyncAsync(cancellationToken);

        var code = PairingCode.New();
        var passes = await EnrolmentsAsync(keys.Keys, cancellationToken);
        var bundle = JsonSerializer.SerializeToUtf8Bytes(new PairingBundle(Guid.Empty, keys, JoinToken, passes));
        await Remote().PutPairingAsync(code.PairingId, code.Seal(bundle), cancellationToken);
        return code.Text;
    }

    /// <summary>
    /// Joins a household with a code from one of its devices: takes the keys, fetches everything
    /// they open, and makes this device the invited person's. Returns that person's profile id.
    /// </summary>
    /// <exception cref="FormatException">The code is mistyped.</exception>
    /// <exception cref="PairingCodeNotFoundException">The code was used already or has expired.</exception>
    public async Task<Guid> JoinAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        var code = PairingCode.Parse(pairingCode);
        var households = new HouseholdRepository(connection);
        if (households.All().Count > 0)
        {
            throw new InvalidOperationException("This device already has a household, so it can't join another.");
        }

        var sealedBundle = await Remote().ClaimPairingAsync(code.PairingId, cancellationToken)
                           ?? throw new PairingCodeNotFoundException();
        var bundle = JsonSerializer.Deserialize<PairingBundle>(code.Open(sealedBundle))
                     ?? throw new InvalidDataException("The pairing held no keys.");

        return await TakeAsync(bundle.ProfileId, bundle.Keys, bundle.JoinToken, bundle.Enrolments, cancellationToken);
    }

    /// <summary>
    /// Locks the keys this device holds under a passphrase, as text to keep somewhere safe. If every
    /// device is lost, this is the only way back into what the server holds.
    /// </summary>
    /// <exception cref="ArgumentException">The passphrase is too short.</exception>
    public string CreateRecoveryKey(string passphrase, int iterations = RecoveryKey.DefaultIterations)
    {
        var profileId = new HouseholdRepository(connection).CurrentProfileId()
                        ?? throw new InvalidOperationException("Pick who is using this device first.");
        var keys = new Dictionary<string, byte[]>();
        foreach (var scope in HeldScopes())
        {
            keys[scope] = KeyFor(scope)!;
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException("Sync is off, so there are no keys to keep. Turn on sync first.");
        }

        // The key carries its own device token, registered with the server on the next sync, so it
        // still opens the household after a device has been removed (when shared tokens stop working).
        var recoveryDevice = "recovery-" + Guid.NewGuid().ToString("N")[..16];
        var recoveryToken = SealedBox.Base64Url(RandomNumberGenerator.GetBytes(32));
        SaveSetting(RecoveryDeviceKey, recoveryDevice);
        SaveSetting(RecoveryTokenKey, recoveryToken);
        return RecoveryKey.Create(passphrase, new RecoveryContents(profileId, keys, JoinToken, recoveryToken), iterations);
    }

    /// <summary>
    /// Brings a household back on an empty device from a recovery key: takes the keys, fetches
    /// everything they open and makes this device the person it was made on. Returns that profile id.
    /// </summary>
    /// <exception cref="FormatException">The text isn't a whole recovery key.</exception>
    /// <exception cref="CryptographicException">The passphrase is wrong.</exception>
    public async Task<Guid> RestoreAsync(string recoveryKey, string passphrase, CancellationToken cancellationToken = default)
    {
        var contents = RecoveryKey.Open(passphrase, recoveryKey);
        var passes = contents.DeviceToken is { } token ? contents.Keys.Keys.ToDictionary(scope => scope, _ => token) : null;
        if (contents.DeviceToken is { } keyToken)
        {
            // Make sure the key's own token is known to the server, in case it never got there.
            SaveSetting(RecoveryDeviceKey, "recovery-" + Guid.NewGuid().ToString("N")[..16]);
            SaveSetting(RecoveryTokenKey, keyToken);
        }

        return await TakeAsync(contents.ProfileId, contents.Keys, contents.JoinToken, passes, cancellationToken);
    }

    private const string DeviceTokenKey = "sync_device_token";
    private const string RecoveryTokenKey = "sync_recovery_token";
    private const string RecoveryDeviceKey = "sync_recovery_device";

    /// <summary>What this device is called in the household's device list. Defaults to the machine's name.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>This device's id, as the household's device list and the server know it.</summary>
    public string ThisDeviceId => DeviceId();

    /// <summary>Every device that has synced in this household, newest first, including removed ones.</summary>
    public IReadOnlyList<KnownDevice> Devices(Guid householdId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, profile_id, name, added_at, removed_at FROM devices WHERE household_id = $household ORDER BY added_at DESC, rowid DESC";
        command.Parameters.AddWithValue("$household", householdId.ToString());
        using var reader = command.ExecuteReader();
        var devices = new List<KnownDevice>();
        while (reader.Read())
        {
            devices.Add(new KnownDevice(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(0) == DeviceId()));
        }

        return devices;
    }

    /// <summary>
    /// Cuts a lost or given-away device off the household: the server stops accepting it on every
    /// stream this device holds, and it's marked removed in the device list. It keeps what it had
    /// already downloaded. A stream this device doesn't hold (someone else's private one) has to be
    /// cut from one of that person's devices; those are counted in the result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server can't remove devices yet, or this device isn't set up on every stream.</exception>
    public async Task<DeviceRemoval> RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == DeviceId())
        {
            throw new InvalidOperationException("This is the device you're using. Remove it from another device.");
        }

        if (!await Remote().SupportsDevicesAsync(cancellationToken))
        {
            throw new InvalidOperationException("This sync server is too old to remove a single device. Update it first; see docs/sync-server.md.");
        }

        await SyncAsync(cancellationToken);

        var household = Scalar("SELECT household_id FROM devices WHERE id = $id", ("$id", deviceId)) as string;
        var personScope = Scalar("SELECT profile_id FROM devices WHERE id = $id", ("$id", deviceId)) is string profile ? SyncScopes.Person(profile) : null;
        // Only the streams that device could reach: the household's, and the private one of whoever used it.
        var held = HeldScopes();
        var reachable = new[] { household is null ? null : SyncScopes.Household(household), personScope }.OfType<string>().ToList();
        int cut = 0, notHeld = reachable.Count(scope => !held.Contains(scope));
        foreach (var scope in reachable.Where(held.Contains))
        {
            var stream = StreamKey.From(KeyFor(scope)!);

            // After a removal the stream only accepts registered devices, so this one has to be one.
            // A stream the server doesn't have yet is empty, so there's nothing to cut there.
            if (TokenFor(scope, stream) == stream.AccessToken)
            {
                switch (await Remote().RegisterDeviceAsync(stream.StreamId, stream.AccessToken, DeviceId(), DeviceToken(), cancellationToken))
                {
                    case DeviceOutcome.Done:
                        Execute("UPDATE sync_keys SET registered = 1, enrolment = NULL WHERE scope = $scope", ("$scope", scope));
                        break;
                    case DeviceOutcome.UnknownStream:
                        // Nothing there yet. Make the stream now and lock it, so the removed device
                        // can't open it with the shared token once something is written to it.
                        await Remote().PushAsync(stream.StreamId, stream.AccessToken, [], cancellationToken);
                        if (await Remote().RegisterDeviceAsync(stream.StreamId, stream.AccessToken, DeviceId(), DeviceToken(), cancellationToken)
                            != DeviceOutcome.Done)
                        {
                            throw new InvalidOperationException("The sync server wouldn't set this device up. Sync again in a minute, then try once more.");
                        }

                        Execute("UPDATE sync_keys SET registered = 1, enrolment = NULL WHERE scope = $scope", ("$scope", scope));
                        break;
                    default:
                        throw new InvalidOperationException("This device isn't set up with the sync server for every part of the household yet. Sync again in a minute, then try once more.");
                }
            }

            switch (await Remote().RemoveDeviceAsync(stream.StreamId, TokenFor(scope, stream), deviceId, cancellationToken))
            {
                case DeviceOutcome.Done:
                    cut++;
                    break;
                case DeviceOutcome.LastDevice:
                    throw new InvalidOperationException("That's the only device the server knows on part of this household, so removing it would lock everyone out.");
                case DeviceOutcome.Denied:
                    throw new SyncAccessDeniedException(stream.StreamId);
            }
        }

        if (household is not null)
        {
            Execute("UPDATE devices SET removed_at = $now WHERE id = $id AND removed_at IS NULL", ("$id", deviceId), ("$now", Text(_clock.GetUtcNow())));
            await SyncAsync(cancellationToken);
        }

        return new DeviceRemoval(cut, notHeld);
    }

    /// <summary>The token this device uses on a stream: its own once registered, otherwise the stream's shared one.</summary>
    private string TokenFor(string scope, StreamKey stream) =>
        Convert.ToInt64(Scalar("SELECT registered FROM sync_keys WHERE scope = $scope", ("$scope", scope))) == 1 ? DeviceToken() : stream.AccessToken;

    private string DeviceToken() => SecretSetting(DeviceTokenKey);

    private string SecretSetting(string key)
    {
        if (Setting(key) is { } token)
        {
            return token;
        }

        token = SealedBox.Base64Url(RandomNumberGenerator.GetBytes(32));
        SaveSetting(key, token);
        return token;
    }

    /// <summary>
    /// Gives this device (and a recovery key made here, if any) its own token on every stream that
    /// doesn't have one yet. Uses the stream's shared token, or a one-time pass from the device that
    /// invited it. A server without device tokens is left alone.
    /// </summary>
    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var pending = Strings("SELECT scope FROM sync_keys WHERE registered = 0").ToList();
        var recovery = Setting(RecoveryTokenKey) is { Length: > 0 } recoveryToken && Setting(RecoveryDeviceKey) is { Length: > 0 } recoveryDevice
            ? (Device: recoveryDevice, Token: recoveryToken)
            : ((string Device, string Token)?)null;
        if ((pending.Count == 0 && recovery is null) || !await Remote().SupportsDevicesAsync(cancellationToken))
        {
            return;
        }

        foreach (var scope in pending)
        {
            var stream = StreamKey.From(KeyFor(scope)!);
            var pass = Scalar("SELECT enrolment FROM sync_keys WHERE scope = $scope", ("$scope", scope)) as string;
            var outcome = await Remote().RegisterDeviceAsync(stream.StreamId, pass ?? stream.AccessToken, DeviceId(), DeviceToken(), cancellationToken);

            // A pass can be stale (a recovery key whose token never reached the server); the shared
            // token still works while nothing has been removed from the stream.
            if (outcome == DeviceOutcome.Denied && pass is not null)
            {
                outcome = await Remote().RegisterDeviceAsync(stream.StreamId, stream.AccessToken, DeviceId(), DeviceToken(), cancellationToken);
            }

            switch (outcome)
            {
                case DeviceOutcome.Done:
                    Execute("UPDATE sync_keys SET registered = 1, enrolment = NULL WHERE scope = $scope", ("$scope", scope));
                    break;
                case DeviceOutcome.Denied:
                    throw new SyncAccessDeniedException(stream.StreamId);
            }
        }

        if (recovery is { } key)
        {
            var all = true;
            foreach (var scope in HeldScopes())
            {
                var stream = StreamKey.From(KeyFor(scope)!);
                all &= await Remote().RegisterDeviceAsync(stream.StreamId, TokenFor(scope, stream), key.Device, key.Token, cancellationToken)
                    == DeviceOutcome.Done;
            }

            if (all)
            {
                SaveSetting(RecoveryTokenKey, "");
            }
        }
    }

    /// <summary>One-time passes for a device being invited, for each stream it will hold. Empty with an older server.</summary>
    private async Task<Dictionary<string, string>> EnrolmentsAsync(IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        var passes = new Dictionary<string, string>();
        if (!await Remote().SupportsDevicesAsync(cancellationToken))
        {
            return passes;
        }

        foreach (var scope in scopes)
        {
            var stream = StreamKey.From(KeyFor(scope)!);
            var pass = SealedBox.Base64Url(RandomNumberGenerator.GetBytes(32));
            if (await Remote().AddEnrolmentAsync(stream.StreamId, TokenFor(scope, stream), pass, cancellationToken) == DeviceOutcome.Done)
            {
                passes[scope] = pass;
            }
        }

        return passes;
    }

    // This device's own row in the household's device list, so other devices can see and remove it.
    private void NoteThisDevice()
    {
        var households = new HouseholdRepository(connection);
        var profileId = households.CurrentProfileId();
        var household = profileId is { } id ? households.HouseholdOf(id) : households.All() is [var only] ? only : null;
        if (household is null)
        {
            return;
        }

        Execute(
            """
            INSERT INTO devices (id, household_id, profile_id, name, added_at) VALUES ($id, $household, $profile, $name, $now)
            ON CONFLICT (id) DO UPDATE SET profile_id = excluded.profile_id, name = excluded.name
            WHERE devices.profile_id IS NOT excluded.profile_id OR devices.name IS NOT excluded.name
            """,
            ("$id", DeviceId()), ("$household", household.Id.ToString()), ("$profile", (object?)profileId?.ToString() ?? DBNull.Value),
            ("$name", string.IsNullOrWhiteSpace(DeviceName) ? "A device" : DeviceName.Trim()), ("$now", Text(_clock.GetUtcNow())));
    }

    private static string Text(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private ISyncServer Remote()
    {
        server.JoinToken = JoinToken;
        return server;
    }

    private async Task<Guid> TakeAsync(Guid profileId, IReadOnlyDictionary<string, byte[]> keys, string? joinToken,
        IReadOnlyDictionary<string, string>? passes, CancellationToken cancellationToken)
    {
        var households = new HouseholdRepository(connection);
        if (households.All().Count > 0)
        {
            throw new InvalidOperationException("This device already has a household, so it can't join another.");
        }

        foreach (var (scope, key) in keys)
        {
            if (!(scope.StartsWith(SyncScopes.HouseholdPrefix, StringComparison.Ordinal) || scope.StartsWith(SyncScopes.PersonPrefix, StringComparison.Ordinal))
                || key.Length != 32)
            {
                throw new InvalidDataException("The keys held one this app doesn't recognise.");
            }

            Execute("INSERT INTO sync_keys (scope, key, enrolment) VALUES ($scope, $key, $pass) ON CONFLICT (scope) DO NOTHING",
                ("$scope", scope), ("$key", key), ("$pass", (object?)passes?.GetValueOrDefault(scope) ?? DBNull.Value));
        }

        SetJoinToken(joinToken);
        await SyncAsync(cancellationToken);
        // The assistant's code is for nobody in particular: it acts for the household.
        if (profileId != Guid.Empty)
        {
            households.SetCurrentProfile(profileId);
        }

        // Again, now it knows whose it is, so it shows in the household's device list.
        await SyncAsync(cancellationToken);
        return profileId;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        NoteThisDevice();
        await RegisterAsync(cancellationToken);
        RecordLocalChanges();

        // Household streams first, since private rows refer to the people and tasks in them.
        var received = 0;
        foreach (var scope in HeldScopes().OrderBy(scope => scope.StartsWith(SyncScopes.PersonPrefix, StringComparison.Ordinal)).ThenBy(scope => scope, StringComparer.Ordinal))
        {
            received += await PullAsync(scope, cancellationToken);
        }

        var sent = await PushAsync(cancellationToken);

        // A stream this device just created only exists on the server now, so it can be registered.
        await RegisterAsync(cancellationToken);
        return new SyncResult(sent, received);
    }

    /// <summary>
    /// Compares every syncing row with what was last sent or received, and queues a new version of
    /// each one that changed, moved stream, or disappeared. A row whose stream key this device
    /// doesn't hold stays on this device.
    /// </summary>
    private void RecordLocalChanges()
    {
        using var transaction = connection.BeginTransaction();
        var held = HeldScopes();
        var seen = new HashSet<(string Table, string Key)>();
        var taskScopes = new Dictionary<string, string>();

        foreach (var table in SyncTable.All)
        {
            foreach (var row in ReadRows(table.Select))
            {
                var key = KeyOf(table, row);
                var scope = table.Scope(row, taskScopes);
                if (table.Name == "tasks" && scope is not null)
                {
                    taskScopes[key] = scope;
                }

                if (scope is null || !held.Contains(scope))
                {
                    continue;
                }

                seen.Add((table.Name, key));
                var json = RowJson(row);
                var hash = Hash(json);
                var record = RecordOf(table.Name, key);
                if (record is { Hash: { } previous } && previous == hash && record.Scope == scope)
                {
                    continue;
                }

                // Moving stream: take it out of the old one first. The new copy gets the later
                // version, so a device holding both streams keeps it whichever order they arrive in.
                if (record is { Hash: not null } moved && moved.Scope != scope && held.Contains(moved.Scope))
                {
                    Enqueue(moved.Scope, table.Name, key, NextVersion(), row: null);
                }

                var version = NextVersion();
                Enqueue(scope, table.Name, key, version, json);
                SaveRecord(table.Name, key, scope, version, hash);
            }
        }

        foreach (var record in LiveRecords().Where(record => !seen.Contains((record.Table, record.Key)) && held.Contains(record.Scope)))
        {
            var version = NextVersion();
            Enqueue(record.Scope, record.Table, record.Key, version, row: null);
            SaveRecord(record.Table, record.Key, record.Scope, version, hash: null);
        }

        transaction.Commit();
    }

    private async Task<int> PullAsync(string scope, CancellationToken cancellationToken)
    {
        var stream = StreamKey.From(KeyFor(scope)!);
        var after = Convert.ToInt64(Scalar("SELECT pulled_through FROM sync_keys WHERE scope = $scope", ("$scope", scope)));
        var applied = 0;

        while (true)
        {
            var page = await Remote().PullAsync(stream.StreamId, TokenFor(scope, stream), after, cancellationToken);
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var change in page.Changes)
                {
                    if (Apply(scope, JsonNode.Parse(stream.Open(change.Data))!.AsObject()))
                    {
                        applied++;
                    }

                    after = change.Seq;
                }

                Execute("UPDATE sync_keys SET pulled_through = $after WHERE scope = $scope", ("$after", after), ("$scope", scope));
                transaction.Commit();
            }

            if (!page.More || page.Changes.Count == 0)
            {
                return applied;
            }
        }
    }

    /// <summary>Applies another device's change unless this device already has that version or a later one.</summary>
    private bool Apply(string scope, JsonObject change)
    {
        var tableName = (string)change["t"]!;
        var key = (string)change["k"]!;
        var version = (string)change["v"]!;

        // A newer app version may sync kinds of rows this one doesn't know yet.
        if (SyncTable.All.FirstOrDefault(table => table.Name == tableName) is not { } table)
        {
            return false;
        }

        if (RecordOf(table.Name, key) is { } record && string.CompareOrdinal(record.Version, version) >= 0)
        {
            return false;
        }

        ObserveVersion(version);
        Execute("SAVEPOINT apply_change");
        try
        {
            if (change["r"] is JsonObject row)
            {
                Upsert(table, row);
            }
            else
            {
                Delete(table, key);
            }

            Execute("RELEASE apply_change");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == ConstraintFailed)
        {
            // It refers to something that no longer exists here, such as a reminder for a task that
            // has since been made private. There is nothing to attach it to, so it is dropped.
            Execute("ROLLBACK TO apply_change");
            Execute("RELEASE apply_change");
            return false;
        }

        var stored = ReadRows(table.Select + $" {(table.Select.Contains("WHERE", StringComparison.Ordinal) ? "AND" : "WHERE")} {KeyCondition(table)}",
                KeyParameters(table, key))
            .FirstOrDefault();
        SaveRecord(table.Name, key, scope, version, stored is null ? null : Hash(RowJson(stored)));
        Execute("DELETE FROM sync_outbox WHERE table_name = $table AND row_key = $key AND version < $version",
            ("$table", table.Name), ("$key", key), ("$version", version));
        return true;
    }

    private void Upsert(SyncTable table, JsonObject row)
    {
        var values = row.ToDictionary(pair => pair.Key, pair => Value(pair.Value));
        if (table.Upsert is { } custom)
        {
            Execute(custom, values.Select(pair => ("$" + pair.Key, pair.Value)).ToArray());
            return;
        }

        // Only columns this version of the app knows, so a newer device's extra columns are ignored.
        var columns = values.Keys.Where(ColumnsOf(table.Name).Contains).ToList();
        var updates = columns.Where(column => !table.KeyColumns.Contains(column)).Select(column => $"{column} = excluded.{column}").ToList();
        var sql = $"""
                   INSERT INTO {table.Name} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(column => "$" + column))})
                   ON CONFLICT ({string.Join(", ", table.KeyColumns)}) DO {(updates.Count == 0 ? "NOTHING" : "UPDATE SET " + string.Join(", ", updates))}
                   """;
        Execute(sql, columns.Select(column => ("$" + column, values[column])).ToArray());
    }

    private void Delete(SyncTable table, string key) =>
        Execute(table.Delete ?? $"DELETE FROM {table.Name} WHERE {KeyCondition(table)}", KeyParameters(table, key));

    private async Task<int> PushAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var scope in Strings("SELECT DISTINCT scope FROM sync_outbox"))
        {
            var stream = StreamKey.From(KeyFor(scope)!);
            while (true)
            {
                var batch = new List<(long Id, string Change)>();
                using (var command = Command("SELECT id, change FROM sync_outbox WHERE scope = $scope ORDER BY id LIMIT $limit",
                           ("$scope", scope), ("$limit", PushBatchSize)))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        batch.Add((reader.GetInt64(0), reader.GetString(1)));
                    }
                }

                if (batch.Count == 0)
                {
                    break;
                }

                await Remote().PushAsync(stream.StreamId, TokenFor(scope, stream),
                    batch.Select(entry => stream.Seal(Encoding.UTF8.GetBytes(entry.Change))).ToList(), cancellationToken);
                Execute("DELETE FROM sync_outbox WHERE scope = $scope AND id <= $last", ("$scope", scope), ("$last", batch[^1].Id));
                sent += batch.Count;
            }
        }

        return sent;
    }

    private void Enqueue(string scope, string table, string key, string version, string? row)
    {
        var change = new JsonObject
        {
            ["t"] = table,
            ["k"] = key,
            ["v"] = version,
            ["r"] = row is null ? null : JsonNode.Parse(row),
        };
        Execute("INSERT INTO sync_outbox (scope, table_name, row_key, version, change) VALUES ($scope, $table, $key, $version, $change)",
            ("$scope", scope), ("$table", table), ("$key", key), ("$version", version), ("$change", change.ToJsonString()));
    }

    /// <summary>
    /// A version that sorts after every version this device has made or seen: a millisecond clock
    /// that never runs backwards, then this device's id to break ties.
    /// </summary>
    private string NextVersion()
    {
        var last = Convert.ToInt64(Setting(ClockKey) ?? "0", CultureInfo.InvariantCulture);
        var now = Math.Max(_clock.GetUtcNow().ToUnixTimeMilliseconds(), last + 1);
        SaveSetting(ClockKey, now.ToString(CultureInfo.InvariantCulture));
        return $"{now:D13}-{DeviceId()}";
    }

    private void ObserveVersion(string version)
    {
        var seen = long.Parse(version.AsSpan(0, version.IndexOf('-')), CultureInfo.InvariantCulture);
        if (seen > Convert.ToInt64(Setting(ClockKey) ?? "0", CultureInfo.InvariantCulture))
        {
            SaveSetting(ClockKey, seen.ToString(CultureInfo.InvariantCulture));
        }
    }

    private string DeviceId()
    {
        if (Setting(DeviceIdKey) is { } id)
        {
            return id;
        }

        id = Guid.NewGuid().ToString("N");
        SaveSetting(DeviceIdKey, id);
        return id;
    }

    private void AddKey(string scope) =>
        Execute("INSERT INTO sync_keys (scope, key) VALUES ($scope, $key) ON CONFLICT (scope) DO NOTHING",
            ("$scope", scope), ("$key", RandomNumberGenerator.GetBytes(32)));

    private byte[]? KeyFor(string scope) => Scalar("SELECT key FROM sync_keys WHERE scope = $scope", ("$scope", scope)) as byte[];

    private HashSet<string> HeldScopes() => Strings("SELECT scope FROM sync_keys").ToHashSet();

    private SyncRecord? RecordOf(string table, string key)
    {
        using var command = Command("SELECT scope, version, hash FROM sync_records WHERE table_name = $table AND row_key = $key",
            ("$table", table), ("$key", key));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SyncRecord(table, key, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    private List<SyncRecord> LiveRecords()
    {
        using var command = Command("SELECT table_name, row_key, scope, version, hash FROM sync_records WHERE hash IS NOT NULL");
        using var reader = command.ExecuteReader();
        var records = new List<SyncRecord>();
        while (reader.Read())
        {
            records.Add(new SyncRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return records;
    }

    private void SaveRecord(string table, string key, string scope, string version, string? hash) => Execute(
        """
        INSERT INTO sync_records (table_name, row_key, scope, version, hash) VALUES ($table, $key, $scope, $version, $hash)
        ON CONFLICT (table_name, row_key) DO UPDATE SET scope = excluded.scope, version = excluded.version, hash = excluded.hash
        """,
        ("$table", table), ("$key", key), ("$scope", scope), ("$version", version), ("$hash", hash));

    private static string KeyOf(SyncTable table, IReadOnlyDictionary<string, object?> row) =>
        string.Join("|", table.KeyColumns.Select(column => Convert.ToString(row[column], CultureInfo.InvariantCulture)));

    private static string KeyCondition(SyncTable table) =>
        string.Join(" AND ", table.KeyColumns.Select(column => $"{column} = $key_{column}"));

    private static (string Name, object? Value)[] KeyParameters(SyncTable table, string key)
    {
        var parts = key.Split('|');
        return table.KeyColumns.Select((column, i) => ("$key_" + column, (object?)parts[i])).ToArray();
    }

    private static string RowJson(IReadOnlyDictionary<string, object?> row)
    {
        var json = new JsonObject();
        foreach (var (column, value) in row)
        {
            json[column] = value switch
            {
                null => null,
                string text => JsonValue.Create(text),
                long number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                _ => throw new InvalidDataException($"Column {column} holds a {value.GetType().Name}, which doesn't sync."),
            };
        }

        return json.ToJsonString();
    }

    private static object? Value(JsonNode? node) => node?.GetValueKind() switch
    {
        null or JsonValueKind.Null => null,
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number when node.AsValue().TryGetValue<long>(out var whole) => whole,
        JsonValueKind.Number => node.GetValue<double>(),
        var kind => throw new InvalidDataException($"A synced value can't be {kind}."),
    };

    private static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private HashSet<string> ColumnsOf(string table)
    {
        if (!_columns.TryGetValue(table, out var columns))
        {
            columns = Strings($"SELECT name FROM pragma_table_info('{table}')").ToHashSet(StringComparer.Ordinal);
            _columns[table] = columns;
        }

        return columns;
    }

    private List<Dictionary<string, object?>> ReadRows(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private string? Setting(string key) => Scalar("SELECT value FROM device_settings WHERE key = $key", ("$key", key)) as string;

    private void SaveSetting(string key, string value) => Execute(
        "INSERT INTO device_settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
        ("$key", key), ("$value", value));

    private List<string> Strings(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private sealed record SyncRecord(string Table, string Key, string Scope, string Version, string? Hash);

    /// <summary>What a pairing code carries: who the new device is for, and the keys they need.</summary>
    private sealed record PairingBundle(Guid ProfileId, Dictionary<string, byte[]> Keys, string? JoinToken = null,
        Dictionary<string, string>? Enrolments = null);
}

/// <summary>A device in the household's list.</summary>
/// <param name="ProfileId">Who last used it.</param>
/// <param name="RemovedAt">When it was cut off, or null while it's in use.</param>
public sealed record KnownDevice(string Id, Guid? ProfileId, string Name, DateTimeOffset AddedAt, DateTimeOffset? RemovedAt, bool IsThisDevice);

/// <summary>What removing a device did.</summary>
/// <param name="StreamsCut">How many streams the server stopped accepting it on.</param>
/// <param name="StreamsNotHeld">Streams this device can't cut it from (someone else's private one).</param>
public sealed record DeviceRemoval(int StreamsCut, int StreamsNotHeld);
