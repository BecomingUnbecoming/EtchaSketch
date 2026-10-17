using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtchaSketch.Data.Profiles;
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
/// device only holds the keys it was given, so Jordy's phone never receives Lacy's private tasks.
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

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, HashSet<string>> _columns = [];

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
        var bundle = JsonSerializer.SerializeToUtf8Bytes(new PairingBundle(profileId, keys));
        await server.PutPairingAsync(code.PairingId, code.Seal(bundle), cancellationToken);
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

        var sealedBundle = await server.ClaimPairingAsync(code.PairingId, cancellationToken)
                           ?? throw new PairingCodeNotFoundException();
        var bundle = JsonSerializer.Deserialize<PairingBundle>(code.Open(sealedBundle))
                     ?? throw new InvalidDataException("The pairing held no keys.");

        foreach (var (scope, key) in bundle.Keys)
        {
            if (!(scope.StartsWith(SyncScopes.HouseholdPrefix, StringComparison.Ordinal) || scope.StartsWith(SyncScopes.PersonPrefix, StringComparison.Ordinal))
                || key.Length != 32)
            {
                throw new InvalidDataException("The pairing held a key this app doesn't recognise.");
            }

            Execute("INSERT INTO sync_keys (scope, key) VALUES ($scope, $key) ON CONFLICT (scope) DO NOTHING", ("$scope", scope), ("$key", key));
        }

        await SyncAsync(cancellationToken);
        households.SetCurrentProfile(bundle.ProfileId);
        return bundle.ProfileId;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        RecordLocalChanges();

        // Household streams first, since private rows refer to the people and tasks in them.
        var received = 0;
        foreach (var scope in HeldScopes().OrderBy(scope => scope.StartsWith(SyncScopes.PersonPrefix, StringComparison.Ordinal)).ThenBy(scope => scope, StringComparer.Ordinal))
        {
            received += await PullAsync(scope, cancellationToken);
        }

        var sent = await PushAsync(cancellationToken);
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
            var page = await server.PullAsync(stream.StreamId, stream.AccessToken, after, cancellationToken);
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

                await server.PushAsync(stream.StreamId, stream.AccessToken,
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
    private sealed record PairingBundle(Guid ProfileId, Dictionary<string, byte[]> Keys);
}
