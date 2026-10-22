namespace EtchaSketch.Core.Notifications;

public enum NotificationKind
{
    DueToday,
    DueNow,
    DueShortly,
    DueSoon,
    Overdue,
    AssignedToYou,
    Expiring,
    WarrantyEnding,
    UpkeepDue,

    /// <summary>A kind this version doesn't know, made by a newer version on another device.</summary>
    Other,
}
