using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Tasks;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>How often a task comes back, such as every week or every 3 days.</summary>
public sealed record Recurrence : ITiered
{
    public Recurrence(RecurrenceFrequency frequency, int interval = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);
        Frequency = frequency;
        Interval = interval;
    }

    [Tier(PrivacyTier.Household)]
    public RecurrenceFrequency Frequency { get; }

    [Tier(PrivacyTier.Household)]
    public int Interval { get; }

    // A schedule is never more private than the task it belongs to, which the task's own tier decides.
    public PrivacyTier Tier => PrivacyTier.Household;

    /// <summary>The occurrence one interval after <paramref name="date"/>.</summary>
    public DateOnly After(DateOnly date) => Frequency switch
    {
        RecurrenceFrequency.Daily => date.AddDays(Interval),
        RecurrenceFrequency.Weekly => date.AddDays(7 * Interval),
        RecurrenceFrequency.Monthly => date.AddMonths(Interval),
        _ => throw new InvalidOperationException($"Unknown frequency {Frequency}."),
    };

    /// <summary>
    /// The first occurrence after both the current due date and the day it was done, so a chore done
    /// late skips the dates it missed instead of coming back already overdue.
    /// </summary>
    public DateOnly NextAfter(DateOnly due, DateOnly completedOn)
    {
        var next = After(due);
        while (next <= completedOn)
        {
            next = After(next);
        }

        return next;
    }
}
