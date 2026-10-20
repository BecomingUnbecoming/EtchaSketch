using System.Globalization;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Tasks;

namespace EtchaSketch.Data.Notifications;

/// <summary>
/// Turns the household's tasks into reminders: due today (or, with a due time, due now and a set
/// number of minutes before), due soon, overdue, and newly given to you. A task reminds whoever it
/// is assigned to, or its owner when nobody is, and only ever someone allowed to see it. Safe to run
/// as often as you like; each reminder is raised once.
/// </summary>
/// <remarks>
/// Times are compared as wall-clock time in <c>now</c>'s offset, so a task due at 14:05 reminds at
/// 14:05 on either side of a clock change. Each reminder has a window: it is raised by the first
/// check inside it, and a check after the window has closed raises the next one instead (opening the
/// app the day after a task was due says "overdue", not "due today").
/// </remarks>
public sealed class Reminders(TaskRepository tasks, NotificationRepository notifications)
{
    /// <summary>When a reminder with no time of its own reaches a phone: first thing, unless quiet hours end later.</summary>
    public static readonly TimeOnly Morning = new(8, 0);

    /// <summary>Raises any reminders that are due and returns how many were new.</summary>
    public int Check(Household household, DateTimeOffset now)
    {
        var wall = now.DateTime;
        var added = 0;
        foreach (var member in household.Members)
        {
            foreach (var (task, reminder) in RemindersOf(household, member))
            {
                if (reminder.From > wall || wall >= reminder.Until)
                {
                    continue;
                }

                var notification = new Notification
                {
                    OwnerId = member.Id,
                    Kind = reminder.Kind,
                    Title = reminder.Title(wall),
                    Body = reminder.Body(wall),
                    TaskId = task.Id,
                    CreatedAt = now,
                    Tier = task.Tier,
                };

                // An alarm is the one reminder quiet hours don't hold back: that's what it was set for.
                var urgent = task.Alarm && reminder.Kind is NotificationKind.DueNow;
                if (notifications.Add(notification, reminder.Key, ignoreQuietHours: urgent) is not null)
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>
    /// The reminders one person will get after <paramref name="now"/> and before the horizon, for a
    /// phone to schedule so they arrive with the app closed. Reminders without a time of their own
    /// come in the morning (or when quiet hours end); other reminders falling in quiet hours wait for
    /// them to end, except alarms.
    /// </summary>
    public IReadOnlyList<PlannedReminder> Upcoming(Household household, Guid personId, DateTimeOffset now, TimeSpan horizon)
    {
        var member = household.Members.FirstOrDefault(profile => profile.Id == personId);
        if (member is null)
        {
            return [];
        }

        var wall = now.DateTime;
        var end = wall + horizon;
        var quiet = notifications.QuietHoursFor(personId);
        var planned = new List<PlannedReminder>();
        foreach (var (task, reminder) in RemindersOf(household, member))
        {
            if (reminder.Kind is NotificationKind.AssignedToYou)
            {
                continue;
            }

            var alarm = task.Alarm && reminder.Kind is NotificationKind.DueNow;
            var at = reminder.Timed ? reminder.From : reminder.From.Date + Morning.ToTimeSpan();
            if (!reminder.Timed && quiet is not null)
            {
                at = reminder.From.Date + quiet.End.ToTimeSpan();
            }
            else if (!alarm && quiet is not null && quiet.Contains(TimeOnly.FromDateTime(at)))
            {
                at = quiet.DeliveryTime(new DateTimeOffset(at, TimeSpan.Zero)).DateTime;
            }

            // Already past (it shows in the feed when the app next opens), closed, or too far off.
            if (at <= wall || at >= reminder.Until || at > end)
            {
                continue;
            }

            planned.Add(new PlannedReminder(reminder.Key, task.Id, reminder.Kind, reminder.Title(at), reminder.Body(at), at, alarm));
        }

        return planned.OrderBy(reminder => reminder.At).ToList();
    }

    private IEnumerable<(TaskItem Task, Window Reminder)> RemindersOf(Household household, Profile member) =>
        tasks.VisibleTo(member.Id)
            .Where(task => (task.AssigneeId ?? task.OwnerId) == member.Id)
            .SelectMany(task => WindowsFor(task, member, household).Select(window => (task, window)));

    /// <param name="From">The first wall-clock moment it may be raised.</param>
    /// <param name="Until">The moment it stops applying (exclusive).</param>
    /// <param name="Timed">Whether <paramref name="From"/> is a real time of day, rather than the start of a day.</param>
    /// <param name="Title">The title, given the moment it is raised ("due tomorrow" depends on when that is).</param>
    /// <param name="Body">The body text, given the moment it is raised.</param>
    private sealed record Window(
        NotificationKind Kind, string Key, Func<DateTime, string> Title, Func<DateTime, string?> Body, DateTime From, DateTime Until, bool Timed);

    private static IEnumerable<Window> WindowsFor(TaskItem task, Profile member, Household household)
    {
        if (task.AssigneeId == member.Id && task.OwnerId != member.Id)
        {
            var owner = household.Members.FirstOrDefault(profile => profile.Id == task.OwnerId)?.DisplayName ?? "Someone";
            yield return new(NotificationKind.AssignedToYou, $"assigned:{task.Id}", _ => $"{owner} assigned {task.Title} to you", _ => null,
                DateTime.MinValue, DateTime.MaxValue, Timed: false);
        }

        if (task.Due is not { } due)
        {
            yield break;
        }

        var dueKey = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dayStart = due.ToDateTime(TimeOnly.MinValue);
        var nextDay = dayStart.AddDays(1);
        var dueText = due.ToString("ddd d MMM", CultureInfo.CurrentCulture);

        yield return new(NotificationKind.Overdue, $"overdue:{task.Id}:{dueKey}", _ => $"{task.Title} is overdue", _ => $"It was due {dueText}",
            nextDay, DateTime.MaxValue, Timed: false);

        if (task.DueTime is not { } time)
        {
            yield return new(NotificationKind.DueToday, $"due:{task.Id}:{dueKey}", _ => $"{task.Title} is due today", _ => null,
                dayStart, nextDay, Timed: false);

            if (task.RemindDaysBefore is { } early)
            {
                yield return Early(task, due, early, dueKey, dueText, timed: false);
            }

            yield break;
        }

        // With a due time, every reminder comes at a time of day rather than first thing.
        var dueAt = due.ToDateTime(time);
        var clock = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var timedKey = $"{dueKey}T{clock}";
        yield return new(NotificationKind.DueNow, $"now:{task.Id}:{timedKey}", _ => $"{task.Title} is due now", _ => $"Due at {clock}",
            dueAt, nextDay, Timed: true);

        if (task.RemindMinutesBefore is { } minutes)
        {
            yield return new(NotificationKind.DueShortly, $"before:{task.Id}:{timedKey}:{minutes}", _ => $"{task.Title} is due at {clock}",
                at => $"In {Duration(dueAt - at)}", dueAt.AddMinutes(-minutes), dueAt, Timed: true);
        }

        if (task.RemindDaysBefore is { } days)
        {
            yield return Early(task, due, days, dueKey, $"{dueText} at {clock}", timed: true);
        }
    }

    // From that many days ahead (at the due time, if there is one) until the due day begins.
    private static Window Early(TaskItem task, DateOnly due, int days, string dueKey, string dueText, bool timed)
    {
        var dayStart = due.ToDateTime(TimeOnly.MinValue);
        var from = (task.DueTime is { } time ? due.ToDateTime(time) : dayStart).AddDays(-days);
        return new(NotificationKind.DueSoon, $"soon:{task.Id}:{dueKey}",
            at =>
            {
                var away = due.DayNumber - DateOnly.FromDateTime(at).DayNumber;
                return $"{task.Title} is due {(away == 1 ? "tomorrow" : $"in {away} days")}";
            },
            _ => $"Due {dueText}", from, dayStart, timed);
    }

    // Rounded up, so a reminder 15 minutes ahead never says "14 minutes" because a check ran a second late.
    private static string Duration(TimeSpan left)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        if (minutes < 60)
        {
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        var hourText = hours == 1 ? "1 hour" : $"{hours} hours";
        return rest == 0 ? hourText : $"{hourText} {rest} min";
    }
}

/// <summary>A reminder a device should show at a wall-clock time, even if the app is closed then.</summary>
/// <param name="Key">The same key the feed uses, so a scheduled reminder and its feed entry are one reminder.</param>
/// <param name="At">Local wall-clock time.</param>
/// <param name="Alarm">Show it as an alarm: louder, and repeating until dismissed.</param>
public sealed record PlannedReminder(string Key, Guid TaskId, NotificationKind Kind, string Title, string? Body, DateTime At, bool Alarm);
