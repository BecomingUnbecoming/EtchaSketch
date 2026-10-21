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
/// 14:05 on either side of a clock change.
/// </remarks>
public sealed class Reminders(TaskRepository tasks, NotificationRepository notifications)
{
    /// <summary>Raises any reminders that are due and returns how many were new.</summary>
    public int Check(Household household, DateTimeOffset now)
    {
        var wall = now.DateTime;
        var added = 0;
        foreach (var member in household.Members)
        {
            foreach (var task in tasks.VisibleTo(member.Id).Where(task => (task.AssigneeId ?? task.OwnerId) == member.Id))
            {
                foreach (var (kind, key, title, body) in RemindersFor(task, member, household, wall))
                {
                    var notification = new Notification
                    {
                        OwnerId = member.Id,
                        Kind = kind,
                        Title = title,
                        Body = body,
                        TaskId = task.Id,
                        CreatedAt = now,
                        Tier = task.Tier,
                    };

                    // An alarm is the one reminder quiet hours don't hold back: that's what it was set for.
                    var urgent = task.Alarm && kind is NotificationKind.DueNow;
                    if (notifications.Add(notification, key, ignoreQuietHours: urgent) is not null)
                    {
                        added++;
                    }
                }
            }
        }

        return added;
    }

    private static IEnumerable<(NotificationKind Kind, string Key, string Title, string? Body)> RemindersFor(
        TaskItem task, Profile member, Household household, DateTime wall)
    {
        if (task.AssigneeId == member.Id && task.OwnerId != member.Id)
        {
            var owner = household.Members.FirstOrDefault(profile => profile.Id == task.OwnerId)?.DisplayName ?? "Someone";
            yield return (NotificationKind.AssignedToYou, $"assigned:{task.Id}", $"{owner} assigned {task.Title} to you", null);
        }

        if (task.Due is not { } due)
        {
            yield break;
        }

        var today = DateOnly.FromDateTime(wall);
        var dueKey = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (due < today)
        {
            yield return (NotificationKind.Overdue, $"overdue:{task.Id}:{dueKey}", $"{task.Title} is overdue",
                $"It was due {due.ToString("ddd d MMM", CultureInfo.CurrentCulture)}");
            yield break;
        }

        if (task.DueTime is not { } time)
        {
            if (due == today)
            {
                yield return (NotificationKind.DueToday, $"due:{task.Id}:{dueKey}", $"{task.Title} is due today", null);
            }
            else if (task.RemindDaysBefore is { } early && today >= due.AddDays(-early))
            {
                yield return EarlyReminder(task, due, today, dueKey);
            }

            yield break;
        }

        // With a due time, every reminder comes at a time of day rather than first thing.
        var dueAt = due.ToDateTime(time);
        var clock = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var timedKey = $"{dueKey}T{clock}";
        if (wall >= dueAt)
        {
            yield return (NotificationKind.DueNow, $"now:{task.Id}:{timedKey}", $"{task.Title} is due now", $"Due at {clock}");
        }
        else if (task.RemindMinutesBefore is { } minutes && wall >= dueAt.AddMinutes(-minutes))
        {
            yield return (NotificationKind.DueShortly, $"before:{task.Id}:{timedKey}:{minutes}",
                $"{task.Title} is due at {clock}", $"In {Duration(dueAt - wall)}");
        }
        else if (due > today && task.RemindDaysBefore is { } early && wall >= due.AddDays(-early).ToDateTime(time))
        {
            yield return EarlyReminder(task, due, today, dueKey);
        }
    }

    private static (NotificationKind, string, string, string?) EarlyReminder(TaskItem task, DateOnly due, DateOnly today, string dueKey)
    {
        var days = due.DayNumber - today.DayNumber;
        var when = task.DueTime is { } time ? $" at {time.ToString("HH:mm", CultureInfo.InvariantCulture)}" : "";
        return (NotificationKind.DueSoon, $"soon:{task.Id}:{dueKey}", $"{task.Title} is due {(days == 1 ? "tomorrow" : $"in {days} days")}",
            $"Due {due.ToString("ddd d MMM", CultureInfo.CurrentCulture)}{when}");
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
