using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Data.Sync;

/// <summary>
/// One kind of row that syncs: how to read it, what identifies it, and which stream it belongs in.
/// Streams are named by scope: <c>household:{id}</c> for what the whole household shares, and
/// <c>person:{id}</c> for what only that person's devices may hold.
/// </summary>
/// <param name="Select">Reads every row of this kind on this device.</param>
/// <param name="Scope">The stream a row belongs in, given the scopes of tasks already read; null to keep it local.</param>
/// <param name="Upsert">Custom SQL to write a row, with a $parameter per column; null to insert or update <paramref name="Name"/> directly.</param>
/// <param name="Delete">Custom SQL to remove a row by its key columns; null to delete from <paramref name="Name"/>.</param>
internal sealed record SyncTable(
    string Name,
    string Select,
    string[] KeyColumns,
    Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, string>, string?> Scope,
    string? Upsert = null,
    string? Delete = null)
{
    /// <summary>
    /// Everything that syncs, parents before children so rows arrive after what they refer to.
    /// Device settings, such as who is using this device, never sync.
    /// </summary>
    public static IReadOnlyList<SyncTable> All { get; } =
    [
        new("households", "SELECT * FROM households", ["id"], (row, _) => SyncScopes.Household(row["id"])),

        new("profiles", "SELECT * FROM profiles", ["id"], (row, _) => SyncScopes.Household(row["household_id"])),

        // A task's notes are its owner's alone, so they never travel with a shared task; they
        // follow in the owner's own stream as task_notes below.
        new("tasks",
            """
            SELECT id, household_id, owner_id, title, due, priority, assignee_id, recurrence_frequency,
                   recurrence_interval, completed_at, tier, created_at, project, labels, remind_days_before,
                   due_time, remind_minutes_before, alarm, parent_id
            FROM tasks
            """,
            ["id"],
            (row, _) => Convert.ToInt32(row["tier"]) <= (int)PrivacyTier.Household
                ? SyncScopes.Household(row["household_id"])
                : SyncScopes.Person(row["owner_id"])),

        new("task_notes", "SELECT id, owner_id, notes FROM tasks WHERE notes IS NOT NULL", ["id"],
            (row, _) => SyncScopes.Person(row["owner_id"]),
            Upsert: "UPDATE tasks SET notes = $notes WHERE id = $id",
            Delete: "UPDATE tasks SET notes = NULL WHERE id = $id"),

        new("task_completions", "SELECT * FROM task_completions", ["task_id", "profile_id", "completed_at"],
            (row, taskScopes) => taskScopes.GetValueOrDefault(Convert.ToString(row["task_id"])!)),

        // A link between two shared tasks is shared; one touching a private task stays with that task's owner.
        new("task_links", "SELECT * FROM task_links", ["from_task", "to_task", "kind"],
            (row, taskScopes) =>
                taskScopes.GetValueOrDefault(Convert.ToString(row["from_task"])!) is { } from
                && taskScopes.GetValueOrDefault(Convert.ToString(row["to_task"])!) is { } to
                    ? from.StartsWith(SyncScopes.PersonPrefix, StringComparison.Ordinal) ? from : to
                    : null),

        new("task_comments", "SELECT * FROM task_comments", ["id"],
            (row, taskScopes) => taskScopes.GetValueOrDefault(Convert.ToString(row["task_id"])!)),

        new("devices", "SELECT * FROM devices", ["id"], (row, _) => SyncScopes.Household(row["household_id"])),

        // The pantry is the household's. Anything marked more private stays on the device it was made on.
        new("stock_items", "SELECT * FROM stock_items", ["id"], (row, _) => SharedOnly(row)),

        new("shopping_items", "SELECT * FROM shopping_items", ["id"], (row, _) => SharedOnly(row)),

        new("recipes", "SELECT * FROM recipes", ["id"], (row, _) => SharedOnly(row)),

        new("planned_meals", "SELECT * FROM planned_meals", ["id"], (row, _) => SharedOnly(row)),

        new("quiet_hours", "SELECT * FROM quiet_hours", ["profile_id"], (row, _) => SyncScopes.Person(row["profile_id"])),

        new("notifications", "SELECT * FROM notifications", ["id"], (row, _) => SyncScopes.Person(row["recipient_id"])),
    ];

    private static string? SharedOnly(IReadOnlyDictionary<string, object?> row) =>
        Convert.ToInt32(row["tier"]) <= (int)PrivacyTier.Household ? SyncScopes.Household(row["household_id"]) : null;
}

internal static class SyncScopes
{
    public const string HouseholdPrefix = "household:";
    public const string PersonPrefix = "person:";

    public static string Household(object? id) => HouseholdPrefix + id;

    public static string Person(object? id) => PersonPrefix + id;
}
