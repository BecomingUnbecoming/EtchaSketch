using System.Globalization;
using EtchaSketch.Core.Calendar;
using EtchaSketch.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Calendar;

/// <summary>Calendar events and time blocks.</summary>
public sealed class EventRepository(SqliteConnection connection)
{
    public const int MaxTitleLength = 200;

    private const string Columns = "id, household_id, owner_id, title, kind, start_date, start_time, end_date, end_time, location, notes, tier";

    public CalendarEvent Add(CalendarEvent calendarEvent)
    {
        calendarEvent = Validated(calendarEvent);
        Execute($"INSERT INTO calendar_events ({Columns}, created_at) VALUES ($id, $household, $owner, $title, $kind, $startDate, $startTime, $endDate, $endTime, $location, $notes, $tier, $created)",
            [.. Parameters(calendarEvent), ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))]);
        return calendarEvent;
    }

    public CalendarEvent Update(CalendarEvent calendarEvent)
    {
        calendarEvent = Validated(calendarEvent);
        var changed = Execute(
            """
            UPDATE calendar_events SET title = $title, kind = $kind, start_date = $startDate, start_time = $startTime, end_date = $endDate,
                end_time = $endTime, location = $location, notes = $notes, tier = $tier
            WHERE id = $id
            """,
            Parameters(calendarEvent));
        return changed == 0 ? throw new ArgumentException($"There is no event {calendarEvent.Id}.", nameof(calendarEvent)) : calendarEvent;
    }

    public void Delete(Guid id) => Execute("DELETE FROM calendar_events WHERE id = $id", ("$id", id.ToString()));

    public CalendarEvent? Get(Guid id) => Query($"SELECT {Columns} FROM calendar_events WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    /// <summary>
    /// The events one person may see that touch the days from <paramref name="from"/> to
    /// <paramref name="to"/>: their own, and everyone's shared ones without the notes.
    /// </summary>
    public IReadOnlyList<CalendarEvent> VisibleTo(Guid profileId, DateOnly from, DateOnly to) => Query(
            $"""
             SELECT {Columns} FROM calendar_events
             WHERE household_id = (SELECT household_id FROM profiles WHERE id = $profile)
               AND (owner_id = $profile OR tier <= $shared)
               AND start_date <= $to AND COALESCE(end_date, start_date) >= $from
             ORDER BY start_date, start_time IS NOT NULL, start_time, title COLLATE NOCASE
             """,
            ("$profile", profileId.ToString()), ("$shared", (int)PrivacyTier.Household), ("$from", Date(from)), ("$to", Date(to)))
        .Select(found => found.OwnerId == profileId ? found : found with { Notes = null })
        .ToList();

    private static CalendarEvent Validated(CalendarEvent calendarEvent)
    {
        var title = calendarEvent.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength)
        {
            throw new ArgumentException("Give it a title.", nameof(calendarEvent));
        }

        if (calendarEvent.EndDate is { } end && end < calendarEvent.StartDate)
        {
            throw new ArgumentException("It can't end before it starts.", nameof(calendarEvent));
        }

        if (calendarEvent.EndTime is not null && calendarEvent.StartTime is null)
        {
            throw new ArgumentException("An end time needs a start time.", nameof(calendarEvent));
        }

        if (calendarEvent is { StartTime: { } startTime, EndTime: { } endTime } && calendarEvent.LastDay == calendarEvent.StartDate && endTime < startTime)
        {
            throw new ArgumentException("It can't end before it starts.", nameof(calendarEvent));
        }

        var location = calendarEvent.Location?.Trim();
        var notes = calendarEvent.Notes?.Trim();
        return calendarEvent with
        {
            Title = title,
            EndDate = calendarEvent.EndDate is { } last && last > calendarEvent.StartDate ? last : null,
            Location = string.IsNullOrEmpty(location) ? null : location.Length > 200 ? throw new ArgumentException("Keep the place to 200 characters.", nameof(calendarEvent)) : location,
            Notes = string.IsNullOrEmpty(notes) ? null : notes.Length > 20_000 ? throw new ArgumentException("Keep the notes to 20,000 characters.", nameof(calendarEvent)) : notes,
        };
    }

    private static (string, object?)[] Parameters(CalendarEvent e) =>
    [
        ("$id", e.Id.ToString()), ("$household", e.HouseholdId.ToString()), ("$owner", e.OwnerId.ToString()), ("$title", e.Title),
        ("$kind", e.Kind.ToString()), ("$startDate", Date(e.StartDate)), ("$startTime", Time(e.StartTime)), ("$endDate", e.EndDate is { } end ? Date(end) : null),
        ("$endTime", Time(e.EndTime)), ("$location", e.Location), ("$notes", e.Notes), ("$tier", (int)e.Tier),
    ];

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Time(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static TimeOnly? ReadTime(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : TimeOnly.ParseExact(reader.GetString(column), "HH:mm", CultureInfo.InvariantCulture);

    private List<CalendarEvent> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var found = new List<CalendarEvent>();
        while (reader.Read())
        {
            found.Add(new CalendarEvent
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                OwnerId = Guid.Parse(reader.GetString(2)),
                Title = reader.GetString(3),
                // A kind from a newer version shows as a plain event.
                Kind = Enum.TryParse<EventKind>(reader.GetString(4), out var kind) ? kind : EventKind.Event,
                StartDate = DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                StartTime = ReadTime(reader, 6),
                EndDate = reader.IsDBNull(7) ? null : DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                EndTime = ReadTime(reader, 8),
                Location = reader.IsDBNull(9) ? null : reader.GetString(9),
                Notes = reader.IsDBNull(10) ? null : reader.GetString(10),
                Tier = (PrivacyTier)reader.GetInt32(11),
            });
        }

        return found;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }
}
