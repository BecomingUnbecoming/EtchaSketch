using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EtchaSketch.Core.Sync;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Server;

/// <summary>
/// The server's log of sealed changes and waiting pairings. It never holds a key: each stream is
/// filed under an id the devices derive from their key, and guarded by a token only key holders
/// can make. Nothing here can be read without the devices' keys.
/// </summary>
public sealed class SyncStore : IDisposable
{
    public const int PageSize = 500;

    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(15);

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly SyncLimits _limits;
    private readonly Lock _gate = new();
    private bool _disposed;

    public SyncStore(string path, TimeProvider clock, SyncLimits? limits = null)
    {
        SQLitePCL.Batteries_V2.Init();
        _clock = clock;
        _limits = limits ?? new SyncLimits();
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute(
            """
            CREATE TABLE IF NOT EXISTS streams (
                id TEXT PRIMARY KEY,
                token_hash BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS changes (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                stream_id TEXT NOT NULL REFERENCES streams (id),
                data BLOB NOT NULL
            );

            CREATE INDEX IF NOT EXISTS changes_by_stream ON changes (stream_id, seq);

            CREATE TABLE IF NOT EXISTS pairings (
                id TEXT PRIMARY KEY,
                data BLOB NOT NULL,
                expires_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS stream_devices (
                stream_id TEXT NOT NULL REFERENCES streams (id),
                device_id TEXT NOT NULL,
                token_hash BLOB NOT NULL,
                PRIMARY KEY (stream_id, device_id)
            );

            CREATE TABLE IF NOT EXISTS stream_enrolments (
                stream_id TEXT NOT NULL REFERENCES streams (id),
                token_hash BLOB NOT NULL,
                expires_at TEXT NOT NULL
            );
            """);

        // Servers from before device removal have no "locked" column yet.
        if (Scalar("SELECT 1 FROM pragma_table_info('streams') WHERE name = 'locked'") is null)
        {
            Execute("ALTER TABLE streams ADD COLUMN locked INTEGER NOT NULL DEFAULT 0");
        }
    }

    /// <summary>
    /// Appends changes to a stream. The first device to write a stream sets its token; after that
    /// only the same token may use it. Nothing is stored unless the whole push fits the limits.
    /// </summary>
    public PushResult Push(string streamId, string accessToken, IReadOnlyList<byte[]> changes, bool mayCreateStream = true)
    {
        if (changes.Count > _limits.MaxChangesPerPush || changes.Any(change => change.Length > _limits.MaxChangeBytes))
        {
            return PushResult.TooLarge;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            switch (Check(streamId, accessToken))
            {
                case Access.Denied:
                    return PushResult.Denied;
                case Access.Unknown:
                    if (!mayCreateStream)
                    {
                        return PushResult.NeedsJoinToken;
                    }

                    if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM streams"), CultureInfo.InvariantCulture) >= _limits.MaxStreams)
                    {
                        return PushResult.NoRoom;
                    }

                    Execute("INSERT INTO streams (id, token_hash) VALUES ($id, $hash)", ("$id", streamId), ("$hash", Hash(accessToken)));
                    break;
            }

            var stored = Convert.ToInt64(Scalar("SELECT COALESCE(SUM(LENGTH(data)), 0) FROM changes WHERE stream_id = $id", ("$id", streamId)), CultureInfo.InvariantCulture);
            if (stored + changes.Sum(change => (long)change.Length) > _limits.MaxStreamBytes)
            {
                return PushResult.NoRoom;
            }

            foreach (var change in changes)
            {
                Execute("INSERT INTO changes (stream_id, data) VALUES ($id, $data)", ("$id", streamId), ("$data", change));
            }

            transaction.Commit();
            return PushResult.Stored;
        }
    }

    /// <summary>The next page of a stream's changes after a position, or null when the token doesn't match.</summary>
    public PullResponse? Pull(string streamId, string accessToken, long after)
    {
        lock (_gate)
        {
            switch (Check(streamId, accessToken))
            {
                case Access.Denied:
                    return null;
                case Access.Unknown:
                    return new PullResponse([], More: false);
            }

            using var command = Command(
                "SELECT seq, data FROM changes WHERE stream_id = $id AND seq > $after ORDER BY seq LIMIT $limit",
                ("$id", streamId), ("$after", after), ("$limit", PageSize + 1));
            using var reader = command.ExecuteReader();
            var changes = new List<StoredChange>();
            while (reader.Read())
            {
                changes.Add(new StoredChange(reader.GetInt64(0), (byte[])reader.GetValue(1)));
            }

            var more = changes.Count > PageSize;
            return new PullResponse(more ? changes[..PageSize] : changes, more);
        }
    }

    /// <summary>Leaves sealed keys for a new device for fifteen minutes, unless the id is taken or too many are waiting.</summary>
    public PairingResult PutPairing(string pairingId, byte[] data)
    {
        if (data.Length > _limits.MaxChangeBytes)
        {
            return PairingResult.TooLarge;
        }

        lock (_gate)
        {
            RemoveExpiredPairings();
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM pairings"), CultureInfo.InvariantCulture) >= _limits.MaxPendingPairings)
            {
                return PairingResult.Full;
            }

            using var command = Command(
                "INSERT INTO pairings (id, data, expires_at) VALUES ($id, $data, $expires) ON CONFLICT (id) DO NOTHING",
                ("$id", pairingId), ("$data", data), ("$expires", Text(_clock.GetUtcNow() + PairingLifetime)));
            return command.ExecuteNonQuery() == 1 ? PairingResult.Stored : PairingResult.Taken;
        }
    }

    /// <summary>Hands over and forgets the sealed keys under a pairing id, so a code works only once.</summary>
    public byte[]? ClaimPairing(string pairingId)
    {
        lock (_gate)
        {
            RemoveExpiredPairings();
            using var command = Command("DELETE FROM pairings WHERE id = $id RETURNING data", ("$id", pairingId));
            return command.ExecuteScalar() as byte[];
        }
    }

    /// <summary>
    /// Gives a device its own token for a stream. Allowed with any token that already works on the
    /// stream, or once with a pass an existing device left for it (see <see cref="AddEnrolment"/>).
    /// </summary>
    public DeviceResult RegisterDevice(string streamId, string authToken, string deviceId, string deviceToken)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            switch (Check(streamId, authToken))
            {
                case Access.Unknown:
                    return DeviceResult.UnknownStream;
                case Access.Denied when !TakeEnrolment(streamId, authToken):
                    return DeviceResult.Denied;
            }

            var count = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM stream_devices WHERE stream_id = $id AND device_id <> $device",
                ("$id", streamId), ("$device", deviceId)), CultureInfo.InvariantCulture);
            if (count >= _limits.MaxDevicesPerStream)
            {
                return DeviceResult.Full;
            }

            Execute(
                """
                INSERT INTO stream_devices (stream_id, device_id, token_hash) VALUES ($id, $device, $hash)
                ON CONFLICT (stream_id, device_id) DO UPDATE SET token_hash = excluded.token_hash
                """,
                ("$id", streamId), ("$device", deviceId), ("$hash", Hash(deviceToken)));
            transaction.Commit();
            return DeviceResult.Done;
        }
    }

    /// <summary>Leaves a one-time pass a new device can use to register itself, for fifteen minutes.</summary>
    public DeviceResult AddEnrolment(string streamId, string authToken, string enrolmentToken)
    {
        lock (_gate)
        {
            switch (Check(streamId, authToken))
            {
                case Access.Unknown:
                    return DeviceResult.UnknownStream;
                case Access.Denied:
                    return DeviceResult.Denied;
            }

            Execute("DELETE FROM stream_enrolments WHERE expires_at <= $now", ("$now", Text(_clock.GetUtcNow())));
            var waiting = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM stream_enrolments WHERE stream_id = $id", ("$id", streamId)),
                CultureInfo.InvariantCulture);
            if (waiting >= _limits.MaxPendingPairings)
            {
                return DeviceResult.Full;
            }

            Execute("INSERT INTO stream_enrolments (stream_id, token_hash, expires_at) VALUES ($id, $hash, $expires)",
                ("$id", streamId), ("$hash", Hash(enrolmentToken)), ("$expires", Text(_clock.GetUtcNow() + PairingLifetime)));
            return DeviceResult.Done;
        }
    }

    /// <summary>
    /// Cuts a device off a stream. From then on the stream only accepts registered devices' own
    /// tokens, so a removed device can't get back in with the stream's shared token either.
    /// </summary>
    public DeviceResult RemoveDevice(string streamId, string authToken, string deviceId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            switch (Check(streamId, authToken))
            {
                case Access.Unknown:
                    return DeviceResult.UnknownStream;
                case Access.Denied:
                    return DeviceResult.Denied;
            }

            var others = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM stream_devices WHERE stream_id = $id AND device_id <> $device",
                ("$id", streamId), ("$device", deviceId)), CultureInfo.InvariantCulture);
            if (others == 0)
            {
                // Locking a stream with nobody registered would leave it unreachable for good.
                return DeviceResult.LastDevice;
            }

            Execute("DELETE FROM stream_devices WHERE stream_id = $id AND device_id = $device", ("$id", streamId), ("$device", deviceId));
            Execute("UPDATE streams SET locked = 1 WHERE id = $id", ("$id", streamId));
            transaction.Commit();
            return DeviceResult.Done;
        }
    }

    private bool TakeEnrolment(string streamId, string enrolmentToken)
    {
        Execute("DELETE FROM stream_enrolments WHERE expires_at <= $now", ("$now", Text(_clock.GetUtcNow())));
        using var command = Command("DELETE FROM stream_enrolments WHERE stream_id = $id AND token_hash = $hash RETURNING 1",
            ("$id", streamId), ("$hash", Hash(enrolmentToken)));
        return command.ExecuteScalar() is not null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _connection.Dispose();
                _disposed = true;
            }
        }
    }

    // A token works on a stream if it is a registered device's own, or the stream's shared token
    // while no device has ever been removed from it.
    private Access Check(string streamId, string accessToken)
    {
        var hash = Hash(accessToken);
        using (var command = Command("SELECT token_hash, locked FROM streams WHERE id = $id", ("$id", streamId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return Access.Unknown;
            }

            if (reader.GetInt64(1) == 0 && CryptographicOperations.FixedTimeEquals((byte[])reader.GetValue(0), hash))
            {
                return Access.Allowed;
            }
        }

        using var devices = Command("SELECT token_hash FROM stream_devices WHERE stream_id = $id", ("$id", streamId));
        using var rows = devices.ExecuteReader();
        var allowed = false;
        while (rows.Read())
        {
            allowed |= CryptographicOperations.FixedTimeEquals((byte[])rows.GetValue(0), hash);
        }

        return allowed ? Access.Allowed : Access.Denied;
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private void RemoveExpiredPairings() =>
        Execute("DELETE FROM pairings WHERE expires_at <= $now", ("$now", Text(_clock.GetUtcNow())));

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Text(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private enum Access
    {
        Unknown,
        Allowed,
        Denied,
    }
}

/// <summary>What happened to a push.</summary>
public enum PushResult
{
    Stored,
    Denied,
    TooLarge,
    NoRoom,
    NeedsJoinToken,
}

/// <summary>What happened to a request about a stream's devices.</summary>
public enum DeviceResult
{
    Done,
    Denied,
    UnknownStream,
    LastDevice,
    Full,
}

/// <summary>What happened to a new-device code left on the server.</summary>
public enum PairingResult
{
    Stored,
    Taken,
    TooLarge,
    Full,
}
