namespace EtchaSketch.Core.Notifications;

/// <summary>What a person's feed shows right now, and how much is waiting for quiet hours to end.</summary>
/// <param name="Shown">Delivered notifications, newest first.</param>
/// <param name="HeldUntil">When the first held notification will show, or null when nothing is held.</param>
public sealed record NotificationFeed(IReadOnlyList<Notification> Shown, int HeldCount, DateTimeOffset? HeldUntil);
