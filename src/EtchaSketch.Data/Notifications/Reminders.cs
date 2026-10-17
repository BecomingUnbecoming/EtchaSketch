using System.Globalization;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Tasks;

namespace EtchaSketch.Data.Notifications;

/// <summary>
/// Turns the household's tasks into reminders: due today, overdue, and newly given to you. A task
/// reminds whoever it is assigned to, or its owner when nobody is, and only ever someone allowed
/// to see it. Safe to run as often as you like; each reminder is raised once.
/// </summary>
public sealed class Reminders(TaskRepository tasks, NotificationRepository notifications)
{
    /// <summary>Raises any reminders that are due and returns how many were new.</summary>
    public int Check(Household household, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var added = 0;
        foreach (var member in household.Members)
        {
            foreach (var task in tasks.VisibleTo(member.Id).Where(task => (task.AssigneeId ?? task.OwnerId) == member.Id))
            {
                foreach (var (kind, key, title, body) in RemindersFor(task, member, household, today))
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

                    if (notifications.Add(notification, key) is not null)
                    {
                        added++;
                    }
                }
            }
        }

        return added;
    }

    private static IEnumerable<(NotificationKind Kind, string Key, string Title, string? Body)> RemindersFor(
        TaskItem task, Profile member, Household household, DateOnly today)
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

        var dueKey = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (due == today)
        {
            yield return (NotificationKind.DueToday, $"due:{task.Id}:{dueKey}", $"{task.Title} is due today", null);
        }
        else if (due < today)
        {
            yield return (NotificationKind.Overdue, $"overdue:{task.Id}:{dueKey}", $"{task.Title} is overdue",
                $"It was due {due.ToString("ddd d MMM", CultureInfo.CurrentCulture)}");
        }
    }
}
