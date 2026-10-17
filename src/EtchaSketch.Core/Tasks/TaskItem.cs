using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Tasks;

/// <summary>
/// A task or chore. A chore is simply a Household-tier task, usually recurring, that anyone in the
/// household can see and pick up. New tasks are Private to their owner.
/// </summary>
public sealed record TaskItem : ITiered, IOwned
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    /// <summary>Who created the task. Only they see its Private parts.</summary>
    public required Guid OwnerId { get; init; }

    /// <summary>Public, so a task you choose to make Public can show its title on stream.</summary>
    [Tier(PrivacyTier.Public)]
    public required string Title { get; init; }

    [Tier(PrivacyTier.Private)]
    public string? Notes { get; init; }

    [Tier(PrivacyTier.Household)]
    public DateOnly? Due { get; init; }

    [Tier(PrivacyTier.Household)]
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;

    /// <summary>The group a task belongs to, like "Homelab". Shown to whoever can see the task.</summary>
    [Tier(PrivacyTier.Household)]
    public string? Project { get; init; }

    /// <summary>Short tags like "quick-win". Shown to whoever can see the task.</summary>
    [Tier(PrivacyTier.Household)]
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Remind this many days before the due date, as well as on it. Null for no early reminder.</summary>
    [Tier(PrivacyTier.Household)]
    public int? RemindDaysBefore { get; init; }

    [Tier(PrivacyTier.Household)]
    public Guid? AssigneeId { get; init; }

    [Tier(PrivacyTier.Household)]
    public Recurrence? Recurrence { get; init; }

    /// <summary>When a one-off task was finished. Recurring tasks move their due date instead.</summary>
    [Tier(PrivacyTier.Household)]
    public DateTimeOffset? CompletedAt { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Private;
}
