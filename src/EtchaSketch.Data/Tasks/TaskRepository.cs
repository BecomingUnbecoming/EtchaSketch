using System.Globalization;
using System.Text.Json;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Tasks;

/// <summary>Reads and writes tasks and chores, and builds the shared chore board.</summary>
public sealed class TaskRepository(SqliteConnection connection)
{
    private const string Columns =
        "id, household_id, owner_id, title, notes, due, priority, assignee_id, recurrence_frequency, recurrence_interval, completed_at, tier, project, labels, remind_days_before, due_time, remind_minutes_before, alarm, parent_id";

    // Open tasks first by due date (undated last), then most urgent first, then oldest first.
    private const string Order = "ORDER BY due IS NULL, due, priority DESC, rowid";

    public const int MaxProjectLength = 60;
    public const int MaxLabels = 20;
    public const int MaxLabelLength = 40;
    public const int MaxRemindDays = 60;
    public const int MaxRemindMinutes = 24 * 60;
    public const int MaxCommentLength = 4_000;

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
                completed_at = $completed_at, tier = $tier, project = $project, labels = $labels,
                remind_days_before = $remind_days_before, due_time = $due_time,
                remind_minutes_before = $remind_minutes_before, alarm = $alarm, parent_id = $parent_id
            WHERE id = $id
            """,
            Parameters(task));

        if (changed == 0)
        {
            throw new ArgumentException($"There is no task {task.Id}.", nameof(task));
        }
    }

    public TaskItem? Get(Guid id) => Query($"SELECT {Columns} FROM tasks WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    /// <summary>Deletes a task and its subtasks. Their links and comments go with them.</summary>
    public void Delete(Guid id)
    {
        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM tasks WHERE parent_id = $id", ("$id", id.ToString()));
        Execute("DELETE FROM tasks WHERE id = $id", ("$id", id.ToString()));
        transaction.Commit();
    }

    /// <summary>
    /// Marks a task done by someone. A one-off task closes; a recurring one moves to its next due
    /// date and stays open. Either way the completion is logged.
    /// </summary>
    /// <exception cref="TaskBlockedException">A task blocking it isn't done yet, and <paramref name="evenIfBlocked"/> wasn't set.</exception>
    public TaskItem Complete(Guid taskId, Guid byProfileId, DateTimeOffset at, bool evenIfBlocked = false)
    {
        var task = Get(taskId) ?? throw new ArgumentException($"There is no task {taskId}.", nameof(taskId));
        if (!evenIfBlocked && OpenBlockersOf(taskId) is { Count: > 0 } blockers)
        {
            throw new TaskBlockedException(blockers);
        }

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
    public IReadOnlyList<TaskItem> VisibleTo(Guid profileId) => Visible(profileId, includeFinished: false);

    /// <summary>
    /// Everything one person may take away in an export: what <see cref="VisibleTo"/> shows, plus
    /// finished tasks under the same rules.
    /// </summary>
    public IReadOnlyList<TaskItem> ExportableBy(Guid profileId) => Visible(profileId, includeFinished: true);

    private List<TaskItem> Visible(Guid profileId, bool includeFinished) => Query(
        $"""
        SELECT {Columns} FROM tasks
        WHERE household_id = (SELECT household_id FROM profiles WHERE id = $profile)
          AND ($finished OR completed_at IS NULL)
          AND (owner_id = $profile OR tier <= $shared)
        {Order}
        """,
        ("$profile", profileId.ToString()), ("$shared", (int)PrivacyTier.Household), ("$finished", includeFinished ? 1 : 0))
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

    public bool HasSubtasks(Guid taskId) =>
        Query($"SELECT {Columns} FROM tasks WHERE parent_id = $id LIMIT 1", ("$id", taskId.ToString())).Count > 0;

    /// <summary>
    /// Links two tasks. Blocking can't go round in a circle (nothing could ever be finished), and the
    /// same link isn't added twice. Returns false when it was there already.
    /// </summary>
    public bool AddLink(TaskLink link, DateTimeOffset at)
    {
        var from = Get(link.FromTaskId) ?? throw new ArgumentException("That task doesn't exist.", nameof(link));
        var to = Get(link.ToTaskId) ?? throw new ArgumentException("That task doesn't exist.", nameof(link));
        if (from.Id == to.Id || from.HouseholdId != to.HouseholdId)
        {
            throw new ArgumentException("A task can only be linked to another task in the same household.", nameof(link));
        }

        if (Links(from.Id).Any(existing => existing.Kind == link.Kind
                && ((existing.FromTaskId == from.Id && existing.ToTaskId == to.Id)
                    || (link.Kind == TaskLinkKind.Related && existing.FromTaskId == to.Id && existing.ToTaskId == from.Id))))
        {
            return false;
        }

        if (link.Kind == TaskLinkKind.Blocks && Blocks(to.Id, from.Id))
        {
            throw new ArgumentException($"{to.Title} already waits on {from.Title} being done, so {from.Title} can't wait on it.", nameof(link));
        }

        Execute("INSERT INTO task_links (from_task, to_task, kind, created_at) VALUES ($from, $to, $kind, $at)",
            ("$from", from.Id.ToString()), ("$to", to.Id.ToString()), ("$kind", link.Kind.ToString()), ("$at", Text(at)));
        return true;
    }

    public void RemoveLink(TaskLink link) => Execute(
        "DELETE FROM task_links WHERE from_task = $from AND to_task = $to AND kind = $kind",
        ("$from", link.FromTaskId.ToString()), ("$to", link.ToTaskId.ToString()), ("$kind", link.Kind.ToString()));

    /// <summary>A task's links as one person sees them: only links to tasks they can see.</summary>
    public IReadOnlyList<TaskLinkView> LinksOf(Guid taskId, Guid viewerId)
    {
        var visible = ExportableBy(viewerId).ToDictionary(task => task.Id);
        if (!visible.ContainsKey(taskId))
        {
            return [];
        }

        return Links(taskId)
            .Select(link => link.FromTaskId == taskId
                ? (Other: link.ToTaskId, link.Kind, OtherIsFirst: false)
                : (Other: link.FromTaskId, link.Kind, OtherIsFirst: true))
            .Where(link => visible.ContainsKey(link.Other))
            .Select(link => new TaskLinkView(visible[link.Other], link.Kind, link.OtherIsFirst))
            .OrderBy(link => link.Label, StringComparer.Ordinal)
            .ThenBy(link => link.Other.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>The unfinished tasks that block this one.</summary>
    public IReadOnlyList<TaskItem> OpenBlockersOf(Guid taskId) => Query(
        $"SELECT {Columns} FROM tasks WHERE completed_at IS NULL "
        + "AND id IN (SELECT from_task FROM task_links WHERE to_task = $id AND kind = $blocks) " + Order,
        ("$id", taskId.ToString()), ("$blocks", nameof(TaskLinkKind.Blocks)));

    public TaskComment AddComment(Guid taskId, Guid authorId, string text, DateTimeOffset at)
    {
        text = (text ?? "").Trim();
        if (text.Length is 0 or > MaxCommentLength)
        {
            throw new ArgumentException(text.Length == 0 ? "Write something first." : $"A comment can be at most {MaxCommentLength} characters.", nameof(text));
        }

        if (!ExportableBy(authorId).Any(task => task.Id == taskId))
        {
            throw new ArgumentException("You can only comment on a task you can see.", nameof(taskId));
        }

        var comment = new TaskComment(Guid.NewGuid(), taskId, authorId, text, at);
        Execute("INSERT INTO task_comments (id, task_id, author_id, text, created_at) VALUES ($id, $task, $author, $text, $at)",
            ("$id", comment.Id.ToString()), ("$task", taskId.ToString()), ("$author", authorId.ToString()), ("$text", text), ("$at", Text(at)));
        return comment;
    }

    /// <summary>A task's comments, oldest first, if this person can see the task.</summary>
    public IReadOnlyList<TaskComment> CommentsOf(Guid taskId, Guid viewerId)
    {
        if (!ExportableBy(viewerId).Any(task => task.Id == taskId))
        {
            return [];
        }

        using var command = Command("SELECT id, author_id, text, created_at FROM task_comments WHERE task_id = $task ORDER BY created_at, rowid",
            ("$task", taskId.ToString()));
        using var reader = command.ExecuteReader();
        var comments = new List<TaskComment>();
        while (reader.Read())
        {
            comments.Add(new TaskComment(Guid.Parse(reader.GetString(0)), taskId, Guid.Parse(reader.GetString(1)), reader.GetString(2),
                ParseTime(reader.GetString(3))));
        }

        return comments;
    }

    private List<TaskLink> Links(Guid taskId)
    {
        using var command = Command("SELECT from_task, to_task, kind FROM task_links WHERE from_task = $id OR to_task = $id",
            ("$id", taskId.ToString()));
        using var reader = command.ExecuteReader();
        var links = new List<TaskLink>();
        while (reader.Read())
        {
            links.Add(new TaskLink(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Enum.Parse<TaskLinkKind>(reader.GetString(2))));
        }

        return links;
    }

    // Whether a chain of "blocks" links leads from one task to another.
    private bool Blocks(Guid from, Guid to)
    {
        var seen = new HashSet<Guid> { from };
        var next = new Queue<Guid>([from]);
        while (next.TryDequeue(out var current))
        {
            foreach (var link in Links(current).Where(link => link.Kind == TaskLinkKind.Blocks && link.FromTaskId == current))
            {
                if (link.ToTaskId == to)
                {
                    return true;
                }

                if (seen.Add(link.ToTaskId))
                {
                    next.Enqueue(link.ToTaskId);
                }
            }
        }

        return false;
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

        var project = task.Project?.Trim();
        if (project is { Length: > MaxProjectLength })
        {
            throw new ArgumentException($"A project name can be at most {MaxProjectLength} characters.", nameof(task));
        }

        var labels = (task.Labels ?? [])
            .Select(label => label?.Trim() ?? "")
            .Where(label => label.Length > 0)
            .DistinctBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (labels.Count > MaxLabels || labels.Any(label => label.Length > MaxLabelLength))
        {
            throw new ArgumentException($"Use at most {MaxLabels} labels of up to {MaxLabelLength} characters each.", nameof(task));
        }

        if (task.RemindDaysBefore is < 1 or > MaxRemindDays)
        {
            throw new ArgumentException($"An early reminder is 1 to {MaxRemindDays} days before the due date.", nameof(task));
        }

        if (task.DueTime is not null && task.Due is null)
        {
            throw new ArgumentException("Give the task a due date before giving it a time.", nameof(task));
        }

        if (task.RemindMinutesBefore is { } minutes)
        {
            if (task.DueTime is null)
            {
                throw new ArgumentException("A reminder some minutes before needs a due time.", nameof(task));
            }

            if (minutes is < 1 or > MaxRemindMinutes)
            {
                throw new ArgumentException($"A reminder before the due time is 1 minute to {MaxRemindMinutes / 60} hours before.", nameof(task));
            }
        }

        if (task.Alarm && task.DueTime is null)
        {
            throw new ArgumentException("An alarm needs a due time.", nameof(task));
        }

        if (task.ParentId is { } parentId)
        {
            var parent = Get(parentId) ?? throw new ArgumentException("That parent task doesn't exist.", nameof(task));
            if (parentId == task.Id || parent.HouseholdId != task.HouseholdId)
            {
                throw new ArgumentException("A task can only be a step of another task in the same household.", nameof(task));
            }

            if (parent.ParentId is not null || HasSubtasks(task.Id))
            {
                throw new ArgumentException("Subtasks go one level deep: a step can't have steps of its own.", nameof(task));
            }

            // A step is never more open than the task it belongs to.
            if (task.Tier < parent.Tier || (parent.Tier > PrivacyTier.Household && parent.OwnerId != task.OwnerId))
            {
                throw new ArgumentException("A step of a private task has to be private too.", nameof(task));
            }
        }

        if (Get(task.Id) is { } before && task.Tier > before.Tier
            && Query($"SELECT {Columns} FROM tasks WHERE parent_id = $id", ("$id", task.Id.ToString())).Any(child => child.Tier < task.Tier))
        {
            throw new ArgumentException("Make its steps private first, so none is more open than the task.", nameof(task));
        }

        // Times are kept to the minute, so a reminder can't be keyed to a second nobody can see.
        var dueTime = task.DueTime is { } time ? new TimeOnly(time.Hour, time.Minute) : (TimeOnly?)null;

        return task with { Title = title, Project = string.IsNullOrEmpty(project) ? null : project, Labels = labels, DueTime = dueTime };
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
        ("$project", task.Project),
        ("$labels", task.Labels.Count == 0 ? null : JsonSerializer.Serialize(task.Labels)),
        ("$remind_days_before", task.RemindDaysBefore),
        ("$due_time", task.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture)),
        ("$remind_minutes_before", task.RemindMinutesBefore),
        ("$alarm", task.Alarm ? 1 : 0),
        ("$parent_id", task.ParentId?.ToString()),
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
                Project = reader.IsDBNull(12) ? null : reader.GetString(12),
                Labels = reader.IsDBNull(13) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(13)) ?? [],
                RemindDaysBefore = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                DueTime = reader.IsDBNull(15) ? null : TimeOnly.ParseExact(reader.GetString(15), "HH:mm", CultureInfo.InvariantCulture),
                RemindMinutesBefore = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                Alarm = reader.GetInt32(17) != 0,
                ParentId = reader.IsDBNull(18) ? null : Guid.Parse(reader.GetString(18)),
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
