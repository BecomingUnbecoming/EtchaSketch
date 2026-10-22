using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Notifications;

/// <summary>Reads and writes each person's notification feed and their quiet hours.</summary>
public sealed class NotificationRepository(SqliteConnection connection)
{
    private const string Columns = "id, recipient_id, kind, title, body, task_id, tier, created_at, deliver_at, read_at";

    /// <summary>
    /// Adds a notification unless one with the same key already went to the same person, so a
    /// reminder can be raised on every check without repeating itself. Returns null for a repeat.
    /// </summary>
    /// <remarks>
    /// The person's quiet hours decide when it shows, unless <paramref name="ignoreQuietHours"/> (an
    /// alarm). It is stored no more open than Household: notification text never moves toward Public
    /// by itself.
    /// </remarks>
    public Notification? Add(Notification notification, string key, bool ignoreQuietHours = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (Scalar("SELECT 1 FROM profiles WHERE id = $id", ("$id", notification.OwnerId.ToString())) is null)
        {
            throw new ArgumentException($"There is no profile {notification.OwnerId}.", nameof(notification));
        }

        var stored = notification with
        {
            // The same reminder raised on two devices gets the same id, so sync merges them into one.
            Id = IdFor(notification.OwnerId, key),
            Tier = notification.Tier < PrivacyTier.Household ? PrivacyTier.Household : notification.Tier,
            DeliverAt = ignoreQuietHours
                ? notification.CreatedAt
                : QuietHoursFor(notification.OwnerId)?.DeliveryTime(notification.CreatedAt) ?? notification.CreatedAt,
        };

        var added = Execute(
            $"""
            INSERT INTO notifications ({Columns}, dedupe_key)
            VALUES ($id, $recipient, $kind, $title, $body, $task, $tier, $created, $deliver, $read, $key)
            ON CONFLICT (recipient_id, dedupe_key) DO NOTHING
            """,
            ("$id", stored.Id.ToString()), ("$recipient", stored.OwnerId.ToString()), ("$kind", stored.Kind.ToString()),
            ("$title", stored.Title), ("$body", stored.Body), ("$task", stored.TaskId?.ToString()), ("$tier", (int)stored.Tier),
            ("$created", Text(stored.CreatedAt)), ("$deliver", Text(stored.DeliverAt)),
            ("$read", stored.ReadAt is { } read ? Text(read) : null), ("$key", key));

        return added == 0 ? null : stored;
    }

    /// <summary>One person's delivered notifications, newest first, and a count of those still held.</summary>
    public NotificationFeed Feed(Guid recipientId, DateTimeOffset now)
    {
        var shown = Query(
            $"SELECT {Columns} FROM notifications WHERE recipient_id = $recipient AND deliver_at <= $now ORDER BY deliver_at DESC, rowid DESC LIMIT 100",
            ("$recipient", recipientId.ToString()), ("$now", Text(now)));

        using var held = Command(
            "SELECT count(*), min(deliver_at) FROM notifications WHERE recipient_id = $recipient AND deliver_at > $now",
            ("$recipient", recipientId.ToString()), ("$now", Text(now)));
        using var reader = held.ExecuteReader();
        reader.Read();
        return new NotificationFeed(shown, reader.GetInt32(0), reader.IsDBNull(1) ? null : ParseTime(reader.GetString(1)));
    }

    public int UnreadCount(Guid recipientId, DateTimeOffset now) => Convert.ToInt32(Scalar(
        "SELECT count(*) FROM notifications WHERE recipient_id = $recipient AND deliver_at <= $now AND read_at IS NULL",
        ("$recipient", recipientId.ToString()), ("$now", Text(now))));

    /// <summary>
    /// Brings a notification back later: a copy shows at <paramref name="until"/> (after quiet hours, if
    /// that falls inside them) and the original is marked read. Returns the copy.
    /// </summary>
    public Notification Snooze(Guid notificationId, DateTimeOffset until, DateTimeOffset now)
    {
        var original = Query($"SELECT {Columns} FROM notifications WHERE id = $id", ("$id", notificationId.ToString())).SingleOrDefault()
            ?? throw new ArgumentException($"There is no notification {notificationId}.", nameof(notificationId));
        if (until <= now)
        {
            throw new ArgumentException("Snooze until a time that hasn't happened yet.", nameof(until));
        }

        Execute("UPDATE notifications SET read_at = coalesce(read_at, $now) WHERE id = $id",
            ("$id", notificationId.ToString()), ("$now", Text(now)));

        var key = $"snooze:{notificationId}:{Text(until)}";
        return Add(original with { CreatedAt = until, ReadAt = null }, key)
            ?? Query($"SELECT {Columns} FROM notifications WHERE id = $id", ("$id", IdFor(original.OwnerId, key).ToString())).Single();
    }

    /// <summary>Marks everything delivered so far as read. Held notifications stay unread for when they show.</summary>
    public void MarkAllRead(Guid recipientId, DateTimeOffset now) => Execute(
        "UPDATE notifications SET read_at = $now WHERE recipient_id = $recipient AND deliver_at <= $now AND read_at IS NULL",
        ("$recipient", recipientId.ToString()), ("$now", Text(now)));

    public QuietHours? QuietHoursFor(Guid profileId)
    {
        using var command = Command("SELECT start_time, end_time FROM quiet_hours WHERE profile_id = $profile",
            ("$profile", profileId.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new QuietHours(ParseClock(reader.GetString(0)), ParseClock(reader.GetString(1))) : null;
    }

    /// <summary>Sets someone's quiet hours, or turns them off with null.</summary>
    public void SetQuietHours(Guid profileId, QuietHours? hours)
    {
        if (hours is null)
        {
            Execute("DELETE FROM quiet_hours WHERE profile_id = $profile", ("$profile", profileId.ToString()));
            return;
        }

        Execute(
            """
            INSERT INTO quiet_hours (profile_id, start_time, end_time) VALUES ($profile, $start, $end)
            ON CONFLICT (profile_id) DO UPDATE SET start_time = excluded.start_time, end_time = excluded.end_time
            """,
            ("$profile", profileId.ToString()), ("$start", Clock(hours.Start)), ("$end", Clock(hours.End)));
    }

    private static Guid IdFor(Guid recipientId, string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{recipientId}|{key}")).AsSpan(0, 16));

    private List<Notification> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var notifications = new List<Notification>();
        while (reader.Read())
        {
            notifications.Add(new Notification
            {
                Id = Guid.Parse(reader.GetString(0)),
                OwnerId = Guid.Parse(reader.GetString(1)),
                Kind = Enum.TryParse<NotificationKind>(reader.GetString(2), out var kind) ? kind : NotificationKind.Other,
                Title = reader.GetString(3),
                Body = reader.IsDBNull(4) ? null : reader.GetString(4),
                TaskId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                Tier = (PrivacyTier)reader.GetInt32(6),
                CreatedAt = ParseTime(reader.GetString(7)),
                DeliverAt = ParseTime(reader.GetString(8)),
                ReadAt = reader.IsDBNull(9) ? null : ParseTime(reader.GetString(9)),
            });
        }

        return notifications;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
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

    // Times are stored in UTC so they sort and compare as text.
    private static string Text(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Clock(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static TimeOnly ParseClock(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);
}
