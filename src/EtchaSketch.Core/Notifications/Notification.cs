using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Notifications;

/// <summary>
/// One entry in a person's notification feed. It belongs to the person it was sent to, takes the
/// tier of whatever it is about, and is never more open than Household, so notification text
/// can't reach the stream.
/// </summary>
public sealed record Notification : ITiered, IOwned
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Who the notification is for.</summary>
    public required Guid OwnerId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required NotificationKind Kind { get; init; }

    [Tier(PrivacyTier.Household)]
    public required string Title { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Body { get; init; }

    /// <summary>The task it is about, if any.</summary>
    [Tier(PrivacyTier.Household)]
    public Guid? TaskId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it shows in the feed: right away, or when the person's quiet hours end.</summary>
    [Tier(PrivacyTier.Household)]
    public DateTimeOffset DeliverAt { get; init; }

    [Tier(PrivacyTier.Household)]
    public DateTimeOffset? ReadAt { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Private;
}
