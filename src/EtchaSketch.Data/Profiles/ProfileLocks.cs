using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Profiles;

public enum PinOutcome
{
    Correct,
    Wrong,

    /// <summary>Too many wrong guesses; nothing is checked until <see cref="PinCheck.TryAgainAt"/>.</summary>
    Wait,
}

/// <param name="TriesLeft">After a wrong PIN, how many more wrong guesses are allowed before a wait.</param>
public sealed record PinCheck(PinOutcome Outcome, int TriesLeft = 0, DateTimeOffset? TryAgainAt = null);

/// <summary>
/// The PIN each person can set to lock their profile on this device. Only a salted PBKDF2 hash is
/// kept, and wrong guesses past <see cref="FreeTries"/> each double the wait before the next.
/// PINs belong to the device and never sync.
/// </summary>
/// <remarks>
/// The lock keeps people who share a device out of each other's profiles. It isn't what keeps the
/// data safe from someone who copies the database: that is the database key in the OS key store.
/// </remarks>
public sealed class ProfileLocks(SqliteConnection connection, int iterations = ProfileLocks.DefaultIterations)
{
    /// <summary>PBKDF2-SHA256 rounds: slow enough to make each guess cost something, quick enough on a phone.</summary>
    public const int DefaultIterations = 100_000;

    /// <summary>Wrong guesses allowed before the first wait.</summary>
    public const int FreeTries = 5;

    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    public bool HasPin(Guid profileId) => Read(profileId) is not null;

    public bool UsesDeviceUnlock(Guid profileId) => Read(profileId)?.DeviceUnlock == true;

    /// <summary>Sets or replaces a person's PIN, clearing any wrong guesses.</summary>
    public void SetPin(Guid profileId, string pin)
    {
        if (pin.Length is < 4 or > 12 || !pin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A PIN is 4 to 12 digits.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        Execute(
            """
            INSERT INTO profile_locks (profile_id, pin_hash, pin_salt, iterations) VALUES ($id, $hash, $salt, $iterations)
            ON CONFLICT (profile_id) DO UPDATE SET pin_hash = excluded.pin_hash, pin_salt = excluded.pin_salt,
                iterations = excluded.iterations, failed_attempts = 0, wait_until = NULL
            """,
            profileId,
            ("$hash", Hash(pin, salt, iterations)),
            ("$salt", salt),
            ("$iterations", iterations));
    }

    public void RemovePin(Guid profileId) => Execute("DELETE FROM profile_locks WHERE profile_id = $id", profileId);

    /// <summary>Lets the device's own fingerprint or screen lock open this profile too. Needs a PIN to fall back on.</summary>
    public void SetDeviceUnlock(Guid profileId, bool enabled)
    {
        if (!HasPin(profileId))
        {
            throw new InvalidOperationException("Set a PIN before using this device's unlock.");
        }

        Execute("UPDATE profile_locks SET device_unlock = $enabled WHERE profile_id = $id", profileId, ("$enabled", enabled ? 1 : 0));
    }

    /// <summary>Checks a PIN. A person without a PIN is always let in.</summary>
    public PinCheck Check(Guid profileId, string pin, DateTimeOffset now)
    {
        if (Read(profileId) is not { } stored)
        {
            return new PinCheck(PinOutcome.Correct);
        }

        if (stored.WaitUntil is { } waitUntil && now < waitUntil)
        {
            return new PinCheck(PinOutcome.Wait, TryAgainAt: waitUntil);
        }

        if (CryptographicOperations.FixedTimeEquals(Hash(pin, stored.Salt, stored.Iterations), stored.Hash))
        {
            UnlockedAnotherWay(profileId);
            return new PinCheck(PinOutcome.Correct);
        }

        var failures = stored.FailedAttempts + 1;
        DateTimeOffset? tryAgainAt = null;
        if (failures >= FreeTries)
        {
            var doublings = Math.Min(failures - FreeTries, 10);
            var wait = TimeSpan.FromTicks(Math.Min(FirstWait.Ticks << doublings, LongestWait.Ticks));
            tryAgainAt = now + wait;
        }

        Execute(
            "UPDATE profile_locks SET failed_attempts = $failures, wait_until = $wait WHERE profile_id = $id",
            profileId,
            ("$failures", failures),
            ("$wait", tryAgainAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

        return tryAgainAt is { } at
            ? new PinCheck(PinOutcome.Wait, TryAgainAt: at)
            : new PinCheck(PinOutcome.Wrong, TriesLeft: FreeTries - failures);
    }

    /// <summary>Clears wrong guesses after the person proved who they are some other way, like the device's unlock.</summary>
    public void UnlockedAnotherWay(Guid profileId) =>
        Execute("UPDATE profile_locks SET failed_attempts = 0, wait_until = NULL WHERE profile_id = $id", profileId);

    private static byte[] Hash(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);

    private StoredLock? Read(Guid profileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pin_hash, pin_salt, iterations, device_unlock, failed_attempts, wait_until FROM profile_locks WHERE profile_id = $id";
        command.Parameters.AddWithValue("$id", profileId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StoredLock(
            (byte[])reader["pin_hash"],
            (byte[])reader["pin_salt"],
            reader.GetInt32(2),
            reader.GetInt32(3) != 0,
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
    }

    private void Execute(string sql, Guid profileId, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", profileId.ToString());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private sealed record StoredLock(byte[] Hash, byte[] Salt, int Iterations, bool DeviceUnlock, int FailedAttempts, DateTimeOffset? WaitUntil);
}
