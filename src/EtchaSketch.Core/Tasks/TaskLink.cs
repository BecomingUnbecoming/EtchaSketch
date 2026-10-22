namespace EtchaSketch.Core.Tasks;

public enum TaskLinkKind
{
    /// <summary>The first task has to be done before the second can be.</summary>
    Blocks,

    /// <summary>The two tasks belong together, with no order between them.</summary>
    Related,
}

/// <summary>A link between two tasks in the same household, like "Install the SDK blocks Build the APK".</summary>
public sealed record TaskLink(Guid FromTaskId, Guid ToTaskId, TaskLinkKind Kind);

/// <summary>A link as one task sees it: the other task, and whether it blocks this one, is blocked by it, or is related.</summary>
public sealed record TaskLinkView(TaskItem Other, TaskLinkKind Kind, bool OtherIsFirst)
{
    /// <summary>How the link reads from this task's side, like "Blocked by" or "Related".</summary>
    public string Label => (Kind, OtherIsFirst) switch
    {
        (TaskLinkKind.Blocks, true) => "Blocked by",
        (TaskLinkKind.Blocks, false) => "Blocks",
        _ => "Related",
    };
}

/// <summary>A comment on a task. Whoever can see the task can read its comments and add one.</summary>
public sealed record TaskComment(Guid Id, Guid TaskId, Guid AuthorId, string Text, DateTimeOffset CreatedAt);

/// <summary>Thrown when finishing a task that still has unfinished tasks blocking it.</summary>
public sealed class TaskBlockedException(IReadOnlyList<TaskItem> blockers)
    : InvalidOperationException($"Blocked by {string.Join(", ", blockers.Select(blocker => blocker.Title))}.")
{
    public IReadOnlyList<TaskItem> Blockers { get; } = blockers;
}
