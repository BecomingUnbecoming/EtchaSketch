using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Storage;

/// <summary>
/// Creates and upgrades the database tables. Each migration runs once, in order, and SQLite's
/// <c>user_version</c> records how many have run. Never edit a migration that has shipped; add a new one.
/// </summary>
public static class Schema
{
    private static readonly string[] Migrations =
    [
        // 1: households, the people in them, and settings that belong to this device only.
        """
        CREATE TABLE households (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE profiles (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            display_name TEXT NOT NULL,
            role TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX profiles_by_household ON profiles (household_id);

        CREATE TABLE device_settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];

    /// <summary>The version a fully upgraded database reports.</summary>
    public static int Version => Migrations.Length;

    public static void Apply(SqliteConnection connection)
    {
        for (var version = CurrentVersion(connection); version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"{Migrations[version]}\nPRAGMA user_version = {version + 1};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public static int CurrentVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
