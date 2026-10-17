namespace EtchaSketch.Core.Notifications;

/// <summary>
/// A daily stretch of local time when new notifications wait instead of showing. It may run past
/// midnight (22:00 to 07:00). The same start and end means no quiet time at all.
/// </summary>
public sealed record QuietHours(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly time) =>
        Start <= End
            ? time >= Start && time < End
            : time >= Start || time < End;

    /// <summary>When something arriving at <paramref name="at"/> should show: then, or when quiet hours end.</summary>
    public DateTimeOffset DeliveryTime(DateTimeOffset at)
    {
        if (!Contains(TimeOnly.FromDateTime(at.DateTime)))
        {
            return at;
        }

        var end = new DateTimeOffset(DateOnly.FromDateTime(at.DateTime).ToDateTime(End), at.Offset);
        return end > at ? end : end.AddDays(1);
    }
}
