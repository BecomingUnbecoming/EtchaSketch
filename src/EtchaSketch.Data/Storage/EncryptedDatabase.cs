using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Storage;

/// <summary>
/// Opens the app's SQLite database, encrypted with SQLCipher under a random 256-bit key kept in
/// the system's secure storage. The key never touches the disk next to the database.
/// </summary>
public static class EncryptedDatabase
{
    /// <summary>The name the database key is stored under in <see cref="IKeyStore"/>.</summary>
    public const string KeyName = "etchasketch.database-key";

    private const int KeyBytes = 32;

    static EncryptedDatabase() => SQLitePCL.Batteries_V2.Init();

    /// <summary>
    /// Opens the database at <paramref name="path"/>, creating it and its key on first use.
    /// </summary>
    /// <exception cref="DatabaseKeyMissingException">The file exists but its key does not.</exception>
    /// <exception cref="SqliteException">The key doesn't match the file.</exception>
    public static SqliteConnection Open(string path, IKeyStore keyStore)
    {
        var key = LoadOrCreateKey(path, keyStore);

        // Pooling is off so no idle connection keeps the unlocked database open in the background.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            Unlock(connection, key);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] LoadOrCreateKey(string path, IKeyStore keyStore)
    {
        if (keyStore.Load(KeyName) is { } existing)
        {
            return existing;
        }

        // A new key can't open an old file, and saving one would hide that the real key was lost.
        if (File.Exists(path))
        {
            throw new DatabaseKeyMissingException(path);
        }

        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        keyStore.Save(KeyName, key);
        return key;
    }

    private static void Unlock(SqliteConnection connection, byte[] key)
    {
        // A raw hex key skips SQLCipher's passphrase stretching, which a random key doesn't need.
        Execute(connection, $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";");

        // Fail closed if the native library isn't SQLCipher, which would silently ignore the key.
        if (string.IsNullOrEmpty(Scalar(connection, "PRAGMA cipher_version") as string))
        {
            throw new InvalidOperationException("SQLite is not built with SQLCipher, so the database would not be encrypted.");
        }

        // The key is only checked on first read; a wrong key fails here with "file is not a database".
        Scalar(connection, "SELECT count(*) FROM sqlite_master;");

        // SQLite leaves foreign keys off unless each connection asks for them.
        Execute(connection, "PRAGMA foreign_keys = ON;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
