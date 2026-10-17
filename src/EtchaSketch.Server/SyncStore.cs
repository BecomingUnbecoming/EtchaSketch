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
            """);
    }

    /// <summary>
    /// Appends changes to a stream. The first device to write a stream sets its token; after that
    /// only the same token may use it. Nothing is stored unless the whole push fits the limits.
    /// </summary>
    public PushResult Push(string streamId, string accessToken, IReadOnlyList<byte[]> changes)
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

    private Access Check(string streamId, string accessToken)
    {
        using var command = Command("SELECT token_hash FROM streams WHERE id = $id", ("$id", streamId));
        return command.ExecuteScalar() switch
        {
            byte[] stored when CryptographicOperations.FixedTimeEquals(stored, Hash(accessToken)) => Access.Allowed,
            byte[] => Access.Denied,
            _ => Access.Unknown,
        };
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
}

/// <summary>What happened to a new-device code left on the server.</summary>
public enum PairingResult
{
    Stored,
    Taken,
    TooLarge,
    Full,
}
