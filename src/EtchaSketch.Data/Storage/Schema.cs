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

        // 5: each person's PIN on this device, kept only as a salted hash, and how many wrong
        // guesses they have made. Device-local like device_settings, so it never syncs.
        """
        CREATE TABLE profile_locks (
            profile_id TEXT PRIMARY KEY,
            pin_hash BLOB NOT NULL,
            pin_salt BLOB NOT NULL,
            iterations INTEGER NOT NULL,
            device_unlock INTEGER NOT NULL DEFAULT 0,
            failed_attempts INTEGER NOT NULL DEFAULT 0,
            wait_until TEXT
        );
        """,

        // 6: a project, labels and an early reminder on each task. They sync with the task.
        """
        ALTER TABLE tasks ADD COLUMN project TEXT;
        ALTER TABLE tasks ADD COLUMN labels TEXT;
        ALTER TABLE tasks ADD COLUMN remind_days_before INTEGER;
        """,

        // 7: a time of day on the due date, a reminder some minutes before it, and alarm-strength
        // reminders. They sync with the task.
        """
        ALTER TABLE tasks ADD COLUMN due_time TEXT;
        ALTER TABLE tasks ADD COLUMN remind_minutes_before INTEGER;
        ALTER TABLE tasks ADD COLUMN alarm INTEGER NOT NULL DEFAULT 0;
        """,

        // 8: subtasks, links between tasks, and comments. All sync: a subtask and a comment go
        // where their task goes, and a link goes where the more private of its two tasks goes.
        """
        -- No foreign key: sync can deliver a subtask before its parent. A subtask whose parent
        -- isn't here (yet) shows as a top-level task, and deleting a task deletes its subtasks.
        ALTER TABLE tasks ADD COLUMN parent_id TEXT;

        CREATE INDEX tasks_by_parent ON tasks (parent_id);

        CREATE TABLE task_links (
            from_task TEXT NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
            to_task TEXT NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (from_task, to_task, kind)
        );

        CREATE INDEX task_links_by_target ON task_links (to_task);

        CREATE TABLE task_comments (
            id TEXT PRIMARY KEY,
            task_id TEXT NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
            author_id TEXT NOT NULL REFERENCES profiles (id),
            text TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX task_comments_by_task ON task_comments (task_id, created_at);
        """,

        // 9: devices. Each device gets its own token with the sync server for every stream it
        // holds, so one lost device can be cut off without changing anyone else's. The household
        // keeps a list of its devices (it syncs) so any of them can remove another.
        """
        ALTER TABLE sync_keys ADD COLUMN registered INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sync_keys ADD COLUMN enrolment TEXT;

        CREATE TABLE devices (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            profile_id TEXT REFERENCES profiles (id),
            name TEXT NOT NULL,
            added_at TEXT NOT NULL,
            removed_at TEXT
        );
        """,

        // 10: the pantry and the shopping list, shared with the household. Amounts are exact decimal text.
        """
        CREATE TABLE stock_items (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            name TEXT NOT NULL,
            amount TEXT NOT NULL,
            unit TEXT,
            location TEXT,
            best_before TEXT,
            low_at TEXT,
            barcode TEXT,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX stock_items_by_household ON stock_items (household_id);

        CREATE TABLE shopping_items (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            name TEXT NOT NULL,
            amount TEXT,
            unit TEXT,
            stock_item_id TEXT,
            bought_at TEXT,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX shopping_items_by_household ON shopping_items (household_id);
        """,

        // 11: recipes and the meal plan, shared with the household. Ingredients are kept with their
        // recipe as JSON, so a recipe always syncs whole.
        """
        CREATE TABLE recipes (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            name TEXT NOT NULL,
            servings INTEGER,
            instructions TEXT,
            ingredients TEXT NOT NULL,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE planned_meals (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            day TEXT NOT NULL,
            meal TEXT NOT NULL,
            recipe_id TEXT,
            title TEXT NOT NULL,
            cooked_at TEXT,
            tier INTEGER NOT NULL
        );

        CREATE INDEX planned_meals_by_day ON planned_meals (household_id, day);
        """,

        // 12: the household's things (home inventory), shared with the household.
        """
        CREATE TABLE inventory_items (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            name TEXT NOT NULL,
            location TEXT,
            category TEXT,
            serial TEXT,
            purchased_on TEXT,
            price TEXT,
            warranty_ends TEXT,
            manual TEXT,
            notes TEXT,
            borrowed_by TEXT,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX inventory_items_by_household ON inventory_items (household_id);
        """,

        // 13: upkeep (services, battery charging, filters…) and an item's log (fuel, services, repairs).
        // An inventory item with a distance unit is a vehicle.
        """
        ALTER TABLE inventory_items ADD COLUMN distance_unit TEXT;

        CREATE TABLE upkeep (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            item_id TEXT NOT NULL REFERENCES inventory_items (id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            every_days INTEGER,
            every_distance INTEGER,
            last_done_on TEXT NOT NULL,
            last_done_distance INTEGER,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX upkeep_by_item ON upkeep (item_id);

        CREATE TABLE item_log (
            id TEXT PRIMARY KEY,
            household_id TEXT NOT NULL REFERENCES households (id),
            item_id TEXT NOT NULL REFERENCES inventory_items (id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            on_date TEXT NOT NULL,
            distance INTEGER,
            amount TEXT,
            cost TEXT,
            note TEXT,
            upkeep_id TEXT,
            tier INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX item_log_by_item ON item_log (item_id);
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
