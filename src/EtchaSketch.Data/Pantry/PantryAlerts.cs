using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Notifications;

namespace EtchaSketch.Data.Pantry;

/// <summary>
/// Tells the household when something in stock is about to go off. Each item warns each person once
/// per best-before date, however many devices check, because the alert's id comes from its key.
/// </summary>
public sealed class PantryAlerts(PantryRepository pantry, NotificationRepository notifications)
{
    /// <summary>How many days ahead of its best-before date an item warns.</summary>
    public const int DaysAhead = 3;

    public int Check(Household household, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var added = 0;
        foreach (var item in pantry.ExpiringBy(household.Id, today.AddDays(DaysAhead)))
        {
            var date = item.BestBefore!.Value;
            var days = date.DayNumber - today.DayNumber;
            var title = days switch
            {
                < 0 => $"{item.Name} is past its best-before date",
                0 => $"{item.Name} goes off today",
                1 => $"{item.Name} goes off tomorrow",
                _ => $"{item.Name} goes off in {days} days",
            };

            foreach (var member in household.Members)
            {
                var notification = new Notification
                {
                    OwnerId = member.Id,
                    Kind = NotificationKind.Expiring,
                    Title = title,
                    Body = item.Location is { } place ? $"In the {place.ToLowerInvariant()}" : null,
                    CreatedAt = now,
                    Tier = PrivacyTier.Household,
                };

                if (notifications.Add(notification, $"expiry:{item.Id}:{date:yyyy-MM-dd}") is not null)
                {
                    added++;
                }
            }
        }

        return added;
    }
}
