using System.Globalization;
using EtchaSketch.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Calendar;

/// <summary>A calendar followed by its link, like a school's term dates or a holiday list.</summary>
public sealed record CalendarSubscription(Guid Id, Guid HouseholdId, Guid OwnerId, string Name, Uri Url, PrivacyTier Tier);

/// <summary>Calendars subscribed to by link, and what each device last fetched from them.</summary>
public sealed class SubscriptionRepository(SqliteConnection connection)
{
    /// <summary>How big a fetched calendar may be.</summary>
    public const int MaxContentLength = 5 * 1024 * 1024;

    public CalendarSubscription Add(Guid householdId, Guid ownerId, string name, string link, bool shared)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            throw new ArgumentException("Give it a name, like School or Holidays.", nameof(name));
        }

        var url = Link(link);
        var subscription = new CalendarSubscription(Guid.NewGuid(), householdId, ownerId, name, url, shared ? PrivacyTier.Household : PrivacyTier.Private);
        Execute("INSERT INTO calendar_subscriptions (id, household_id, owner_id, name, url, tier, created_at) VALUES ($id, $household, $owner, $name, $url, $tier, $created)",
            ("$id", subscription.Id.ToString()), ("$household", householdId.ToString()), ("$owner", ownerId.ToString()), ("$name", name),
            ("$url", url.ToString()), ("$tier", (int)subscription.Tier), ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        return subscription;
    }

    public void Delete(Guid id) => Execute("DELETE FROM calendar_subscriptions WHERE id = $id", ("$id", id.ToString()));

    /// <summary>One person's own subscriptions and the household's shared ones.</summary>
    public IReadOnlyList<CalendarSubscription> VisibleTo(Guid profileId)
    {
        using var command = Command(
            """
            SELECT id, household_id, owner_id, name, url, tier FROM calendar_subscriptions
            WHERE household_id = (SELECT household_id FROM profiles WHERE id = $profile) AND (owner_id = $profile OR tier <= $shared)
            ORDER BY name COLLATE NOCASE
            """,
            ("$profile", profileId.ToString()), ("$shared", (int)PrivacyTier.Household));
        using var reader = command.ExecuteReader();
        var found = new List<CalendarSubscription>();
        while (reader.Read())
        {
            if (Uri.TryCreate(reader.GetString(4), UriKind.Absolute, out var url))
            {
                found.Add(new CalendarSubscription(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    reader.GetString(3), url, (PrivacyTier)reader.GetInt32(5)));
            }
        }

        return found;
    }

    /// <summary>What this device last fetched, and when, or null before the first fetch.</summary>
    public (DateTimeOffset FetchedAt, string Content)? Cached(Guid subscriptionId)
    {
        using var command = Command("SELECT fetched_at, content FROM subscription_cache WHERE subscription_id = $id", ("$id", subscriptionId.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetString(1))
            : null;
    }

    public void SaveFetched(Guid subscriptionId, string content, DateTimeOffset at) => Execute(
        """
        INSERT INTO subscription_cache (subscription_id, fetched_at, content) VALUES ($id, $at, $content)
        ON CONFLICT (subscription_id) DO UPDATE SET fetched_at = excluded.fetched_at, content = excluded.content
        """,
        ("$id", subscriptionId.ToString()), ("$at", at.ToString("O", CultureInfo.InvariantCulture)), ("$content", content));

    /// <summary>A calendar link: https, http or webcal (which is https for calendars).</summary>
    public static Uri Link(string? link)
    {
        var text = link?.Trim() ?? "";
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text["webcal://".Length..];
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "https" or "http"
            ? url
            : throw new ArgumentException("Paste the calendar's link. It starts with https:// or webcal://.", nameof(link));
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
