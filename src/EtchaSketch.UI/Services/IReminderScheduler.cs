using System.Collections.Generic;
using EtchaSketch.Data.Notifications;

namespace EtchaSketch.UI.Services;

/// <summary>
/// The device's own alarm clock, where the platform has one (Android). It shows reminders at their
/// time even when the app is closed, so they don't depend on the app being open or on a server.
/// </summary>
public interface IReminderScheduler
{
    /// <summary>
    /// Makes the scheduled reminders exactly these, cancelling any others. Called often (every
    /// minute while the home screen is open), so it should do nothing when nothing changed.
    /// </summary>
    void Replace(IReadOnlyList<PlannedReminder> reminders);
}
