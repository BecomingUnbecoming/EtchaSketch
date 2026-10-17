namespace EtchaSketch.Core.Tasks;

/// <summary>One column of the shared chore board: a person's chores, or the unassigned ones.</summary>
/// <param name="AssigneeId">The person, or null for chores anyone can take.</param>
public sealed record ChoreColumn(Guid? AssigneeId, IReadOnlyList<TaskItem> Chores);
