using EtchaSketch.Core.Calendar;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Tasks;

namespace EtchaSketch.Data.Calendar;

/// <summary>
/// One calendar for one person: their tasks and the household's chores (repeats included), events
/// and time blocks, upkeep coming due, warranties ending and planned meals. Only what that person
/// may see, through the same rules as the screens that own each thing.
/// </summary>
public sealed class CalendarFeed(TaskRepository tasks, EventRepository events, InventoryRepository inventory, UpkeepRepository upkeep, MealRepository meals,
    SubscriptionRepository subscriptions, TimeZoneInfo zone)
{
    // Each fetched calendar read once, until it's fetched again.
    private readonly Dictionary<Guid, (DateTimeOffset FetchedAt, IReadOnlyList<Core.Calendar.IcsEvent> Events)> _parsed = [];

    /// <summary>Every entry from <paramref name="from"/> to <paramref name="to"/>, by day, then all-day first, then time.</summary>
    public IReadOnlyList<CalendarEntry> Between(Household household, Guid personId, DateOnly from, DateOnly to)
    {
        var entries = new List<CalendarEntry>();

        foreach (var task in tasks.VisibleTo(personId))
        {
            if (task.Due is not { } due)
            {
                continue;
            }

            var view = PrivacyGateway.Project(task, Audience.ForProfile(personId));
            if (view is null)
            {
                continue;
            }

            var kind = task.Tier <= PrivacyTier.Household ? EntryKind.Chore : EntryKind.Task;
            var title = task.Title;
            var assignee = task.AssigneeId is { } who && who != personId ? household.Members.FirstOrDefault(member => member.Id == who)?.DisplayName : null;
            foreach (var day in Occurrences(due, task.Recurrence, from, to))
            {
                entries.Add(new CalendarEntry(day, task.DueTime, null, title, kind, task.Id, assignee is null ? null : $"for {assignee}"));
            }
        }

        foreach (var found in events.VisibleTo(personId, from, to))
        {
            var kind = found.Kind == EventKind.TimeBlock ? EntryKind.TimeBlock : EntryKind.Event;
            for (var day = Max(found.StartDate, from); day <= Min(found.LastDay, to); day = day.AddDays(1))
            {
                var first = day == found.StartDate;
                var last = day == found.LastDay;
                entries.Add(new CalendarEntry(day, first ? found.StartTime : null, last ? found.EndTime : null, found.Title, kind, found.Id, found.Location));
            }
        }

        var things = inventory.Items(household.Id).ToDictionary(item => item.Id);
        foreach (var item in things.Values)
        {
            if (item.WarrantyEnds is { } ends && ends >= from && ends <= to)
            {
                entries.Add(new CalendarEntry(ends, null, null, $"Warranty ends: {item.Name}", EntryKind.Warranty, item.Id));
            }

            foreach (var job in upkeep.UpkeepFor(item.Id))
            {
                // A job due by date shows on that date; a distance can't be put on a date.
                if (job.NextOn is { } next && next >= from && next <= to)
                {
                    entries.Add(new CalendarEntry(next, null, null, $"{item.Name}: {job.Name}", EntryKind.Upkeep, job.Id));
                }
            }
        }

        foreach (var meal in meals.Meals(household.Id, from, to))
        {
            entries.Add(new CalendarEntry(meal.Day, null, null, $"{meal.Meal}: {meal.Title}", EntryKind.Meal, meal.Id));
        }

        foreach (var subscription in subscriptions.VisibleTo(personId))
        {
            foreach (var ics in Parsed(subscription.Id))
            {
                foreach (var start in Core.Calendar.Ics.Starts(ics, from, to))
                {
                    var last = start.AddDays(ics.ExtraDays);
                    for (var day = Max(start, from); day <= Min(last, to); day = day.AddDays(1))
                    {
                        entries.Add(new CalendarEntry(day, day == start ? ics.Start.Time : null, day == last ? ics.End?.Time is { } end && end != TimeOnly.MinValue ? end : null : null,
                            ics.Summary, EntryKind.Subscribed, subscription.Id, subscription.Name));
                    }
                }
            }
        }

        return entries
            .OrderBy(entry => entry.Day)
            .ThenBy(entry => entry.Start is not null)
            .ThenBy(entry => entry.Start)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<Core.Calendar.IcsEvent> Parsed(Guid subscriptionId)
    {
        if (subscriptions.Cached(subscriptionId) is not { } cached)
        {
            return [];
        }

        if (!_parsed.TryGetValue(subscriptionId, out var parsed) || parsed.FetchedAt != cached.FetchedAt)
        {
            // A changed occurrence (RECURRENCE-ID) replaces that day of its series.
            var all = Core.Calendar.Ics.Parse(cached.Content, zone);
            var moved = all.Where(ics => ics.RecurrenceId is not null).GroupBy(ics => ics.Uid)
                .ToDictionary(group => group.Key, group => group.Select(ics => ics.RecurrenceId!.Value).ToList());
            var events = all.Select(ics => ics.RecurrenceId is null && moved.TryGetValue(ics.Uid, out var days)
                ? ics with { Excluded = [.. ics.Excluded, .. days] }
                : ics).ToList();
            parsed = (cached.FetchedAt, events);
            _parsed[subscriptionId] = parsed;
        }

        return parsed.Events;
    }

    /// <summary>The days a task falls on in a range: its due date, and for a repeating one each repeat after it.</summary>
    public static IEnumerable<DateOnly> Occurrences(DateOnly due, Recurrence? recurrence, DateOnly from, DateOnly to)
    {
        if (recurrence is null)
        {
            if (due >= from && due <= to)
            {
                yield return due;
            }

            yield break;
        }

        var day = due;
        for (var guard = 0; day <= to && guard < 2_000; guard++, day = recurrence.After(day))
        {
            if (day >= from)
            {
                yield return day;
            }
        }
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
