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

        // 2: tasks and chores, and who finished them when.
        """
        CREATE TABLE tasks (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            owner_id TEXT NOT NULL REFERENCES profiles (id),
            title TEXT NOT NULL,
            notes TEXT,
            due TEXT,
            priority INTEGER NOT NULL,
            assignee_id TEXT REFERENCES profiles (id),
            recurrence_frequency TEXT,
            recurrence_interval INTEGER,
            completed_at TEXT,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX tasks_by_household ON tasks (household_id, completed_at);

        CREATE TABLE task_completions (
            task_id TEXT NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
            profile_id TEXT NOT NULL REFERENCES profiles (id),
            completed_at TEXT NOT NULL
        );

        CREATE INDEX task_completions_by_task ON task_completions (task_id);
        """,

        // 3: each person's notification feed, and their quiet hours.
        """
        CREATE TABLE notifications (
            id TEXT PRIMARY KEY,
            recipient_id TEXT NOT NULL REFERENCES profiles (id),
            dedupe_key TEXT NOT NULL,
            kind TEXT NOT NULL,
            title TEXT NOT NULL,
            body TEXT,
            task_id TEXT REFERENCES tasks (id) ON DELETE CASCADE,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            deliver_at TEXT NOT NULL,
            read_at TEXT,
            UNIQUE (recipient_id, dedupe_key)
        );

        CREATE INDEX notifications_by_recipient ON notifications (recipient_id, deliver_at);

        CREATE TABLE quiet_hours (
            profile_id TEXT PRIMARY KEY REFERENCES profiles (id),
            start_time TEXT NOT NULL,
            end_time TEXT NOT NULL
        );
        """,

        // 4: sync. The keys for each stream this device holds, the version of every row it has
        // sent or received, and changes waiting to be sent. Completions get a key so they can sync.
        """
        CREATE TABLE sync_keys (
            scope TEXT PRIMARY KEY,
            key BLOB NOT NULL,
            pulled_through INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE sync_records (
            table_name TEXT NOT NULL,
            row_key TEXT NOT NULL,
            scope TEXT NOT NULL,
            version TEXT NOT NULL,
            hash TEXT,
            PRIMARY KEY (table_name, row_key)
        );

        CREATE TABLE sync_outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            scope TEXT NOT NULL,
            table_name TEXT NOT NULL,
            row_key TEXT NOT NULL,
            version TEXT NOT NULL,
            change TEXT NOT NULL
        );

        CREATE UNIQUE INDEX task_completions_unique ON task_completions (task_id, profile_id, completed_at);
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
