using System.Globalization;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Tasks;

/// <summary>Reads and writes tasks and chores, and builds the shared chore board.</summary>
public sealed class TaskRepository(SqliteConnection connection)
{
    private const string Columns =
        "id, household_id, owner_id, title, notes, due, priority, assignee_id, recurrence_frequency, recurrence_interval, completed_at, tier";

    // Open tasks first by due date (undated last), then most urgent first, then oldest first.
    private const string Order = "ORDER BY due IS NULL, due, priority DESC, rowid";

    private static readonly string ParameterList = string.Join(", ", Columns.Split(", ").Select(column => "$" + column));

    public TaskItem Add(TaskItem task)
    {
        task = Validated(task);
        Execute($"INSERT INTO tasks ({Columns}, created_at) VALUES ({ParameterList}, $created)",
            [.. Parameters(task), ("$created", Text(DateTimeOffset.UtcNow))]);
        return task;
    }

    public void Update(TaskItem task)
    {
        task = Validated(task);
        var changed = Execute(
            """
            UPDATE tasks SET household_id = $household_id, owner_id = $owner_id, title = $title, notes = $notes,
                due = $due, priority = $priority, assignee_id = $assignee_id,
                recurrence_frequency = $recurrence_frequency, recurrence_interval = $recurrence_interval,
                completed_at = $completed_at, tier = $tier
            WHERE id = $id
            """,
            Parameters(task));

        if (changed == 0)
        {
            throw new ArgumentException($"There is no task {task.Id}.", nameof(task));
        }
    }

    public TaskItem? Get(Guid id) => Query($"SELECT {Columns} FROM tasks WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    public void Delete(Guid id) => Execute("DELETE FROM tasks WHERE id = $id", ("$id", id.ToString()));

    /// <summary>
    /// Marks a task done by someone. A one-off task closes; a recurring one moves to its next due
    /// date and stays open. Either way the completion is logged.
    /// </summary>
    public TaskItem Complete(Guid taskId, Guid byProfileId, DateTimeOffset at)
    {
        var task = Get(taskId) ?? throw new ArgumentException($"There is no task {taskId}.", nameof(taskId));

        var completedOn = DateOnly.FromDateTime(at.DateTime);
        var updated = task.Recurrence is { } recurrence
            ? task with { Due = task.Due is { } due ? recurrence.NextAfter(due, completedOn) : recurrence.After(completedOn) }
            : task with { CompletedAt = at };

        using var transaction = connection.BeginTransaction();
        Execute("INSERT INTO task_completions (task_id, profile_id, completed_at) VALUES ($task, $profile, $at)",
            ("$task", taskId.ToString()), ("$profile", byProfileId.ToString()), ("$at", Text(at)));
        Update(updated);
        transaction.Commit();
        return updated;
    }

    public IReadOnlyList<TaskCompletion> Completions(Guid taskId)
    {
        using var command = Command(
            "SELECT profile_id, completed_at FROM task_completions WHERE task_id = $task ORDER BY rowid",
            ("$task", taskId.ToString()));
        using var reader = command.ExecuteReader();
        var completions = new List<TaskCompletion>();
        while (reader.Read())
        {
            completions.Add(new TaskCompletion(taskId, Guid.Parse(reader.GetString(0)), ParseTime(reader.GetString(1))));
        }

        return completions;
    }

    /// <summary>
    /// The open tasks one person may see: everything they own, plus everyone's shared tasks. Other
    /// people's Private tasks, and the notes on their shared ones, are never read out at all.
    /// </summary>
    public IReadOnlyList<TaskItem> VisibleTo(Guid profileId) => Query(
        $"""
        SELECT {Columns} FROM tasks
        WHERE household_id = (SELECT household_id FROM profiles WHERE id = $profile)
          AND completed_at IS NULL
          AND (owner_id = $profile OR tier <= $shared)
        {Order}
        """,
        ("$profile", profileId.ToString()), ("$shared", (int)PrivacyTier.Household))
        .Select(task => task.OwnerId == profileId ? task : task with { Notes = null })
        .ToList();

    /// <summary>The household's shared open chores, one column per member in joining order, then unassigned.</summary>
    public IReadOnlyList<ChoreColumn> ChoreBoard(Guid householdId)
    {
        var chores = Query(
            $"SELECT {Columns} FROM tasks WHERE household_id = $household AND completed_at IS NULL AND tier <= $shared {Order}",
            ("$household", householdId.ToString()), ("$shared", (int)PrivacyTier.Household));

        var columns = MemberIds(householdId)
            .Select(member => new ChoreColumn(member, chores.Where(chore => chore.AssigneeId == member).ToList()))
            .ToList();
        columns.Add(new ChoreColumn(null, chores.Where(chore => chore.AssigneeId is null).ToList()));
        return columns;
    }

    private TaskItem Validated(TaskItem task)
    {
        var title = task.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            throw new ArgumentException("A task needs a title.", nameof(task));
        }

        var members = MemberIds(task.HouseholdId);
        if (!members.Contains(task.OwnerId))
        {
            throw new ArgumentException("The task's owner isn't in its household.", nameof(task));
        }

        if (task.AssigneeId is { } assignee)
        {
            if (!members.Contains(assignee))
            {
                throw new ArgumentException("Tasks can only be assigned to someone in the same household.", nameof(task));
            }

            // Someone else can't do a task they aren't allowed to see.
            if (assignee != task.OwnerId && task.Tier > PrivacyTier.Household)
            {
                throw new ArgumentException("Share a task with the household before assigning it to someone else.", nameof(task));
            }
        }

        return task with { Title = title };
    }

    private List<Guid> MemberIds(Guid householdId)
    {
        using var command = Command("SELECT id FROM profiles WHERE household_id = $household ORDER BY rowid",
            ("$household", householdId.ToString()));
        using var reader = command.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private static (string Name, object? Value)[] Parameters(TaskItem task) =>
    [
        ("$id", task.Id.ToString()),
        ("$household_id", task.HouseholdId.ToString()),
        ("$owner_id", task.OwnerId.ToString()),
        ("$title", task.Title),
        ("$notes", task.Notes),
        ("$due", task.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ("$priority", (int)task.Priority),
        ("$assignee_id", task.AssigneeId?.ToString()),
        ("$recurrence_frequency", task.Recurrence?.Frequency.ToString()),
        ("$recurrence_interval", task.Recurrence?.Interval),
        ("$completed_at", task.CompletedAt is { } at ? Text(at) : null),
        ("$tier", (int)task.Tier),
    ];

    private List<TaskItem> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var tasks = new List<TaskItem>();
        while (reader.Read())
        {
            tasks.Add(new TaskItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                OwnerId = Guid.Parse(reader.GetString(2)),
                Title = reader.GetString(3),
                Notes = reader.IsDBNull(4) ? null : reader.GetString(4),
                Due = reader.IsDBNull(5) ? null : DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Priority = (TaskPriority)reader.GetInt32(6),
                AssigneeId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                Recurrence = reader.IsDBNull(8)
                    ? null
                    : new Recurrence(Enum.Parse<RecurrenceFrequency>(reader.GetString(8)), reader.GetInt32(9)),
                CompletedAt = reader.IsDBNull(10) ? null : ParseTime(reader.GetString(10)),
                Tier = (PrivacyTier)reader.GetInt32(11),
            });
        }

        return tasks;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
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

    private static string Text(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
