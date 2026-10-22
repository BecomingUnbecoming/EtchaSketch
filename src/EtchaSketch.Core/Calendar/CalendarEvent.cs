using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Calendar;

public enum EventKind
{
    /// <summary>Something happening: an appointment, a party, a school trip.</summary>
    Event,

    /// <summary>Time set aside for something, like "Deep work" or "Errands".</summary>
    TimeBlock,
}

/// <summary>
/// Something on the calendar. Times are wall-clock times, like a task's due time, so 14:00 stays
/// 14:00 across a clock change. Without a start time it lasts the whole day (or days). Private to
/// its owner unless shared; its notes are the owner's alone either way.
/// </summary>
public sealed record CalendarEvent : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required Guid OwnerId { get; init; }

    [Tier(PrivacyTier.Public)]
    public required string Title { get; init; }

    [Tier(PrivacyTier.Household)]
    public EventKind Kind { get; init; } = EventKind.Event;

    [Tier(PrivacyTier.Household)]
    public required DateOnly StartDate { get; init; }

    /// <summary>Null for an all-day event.</summary>
    [Tier(PrivacyTier.Household)]
    public TimeOnly? StartTime { get; init; }

    /// <summary>The last day it covers; the start date when it's one day.</summary>
    [Tier(PrivacyTier.Household)]
    public DateOnly? EndDate { get; init; }

    [Tier(PrivacyTier.Household)]
    public TimeOnly? EndTime { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Location { get; init; }

    [Tier(PrivacyTier.Private)]
    public string? Notes { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Private;

    public bool IsAllDay => StartTime is null;

    public DateOnly LastDay => EndDate is { } end && end > StartDate ? end : StartDate;
}

public enum EntryKind
{
    Task,
    Chore,
    Event,
    TimeBlock,
    Upkeep,
    Warranty,
    Meal,
}

/// <summary>
/// One line on the calendar for one day, from whichever module it came from: a task or chore due,
/// an event or time block, upkeep coming due, a warranty ending, a planned meal.
/// </summary>
/// <param name="SourceId">The task, event, upkeep, item or meal it stands for.</param>
public sealed record CalendarEntry(DateOnly Day, TimeOnly? Start, TimeOnly? End, string Title, EntryKind Kind, Guid SourceId, string? Detail = null);
