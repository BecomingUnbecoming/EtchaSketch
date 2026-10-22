using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;

namespace EtchaSketch.Data.Tasks;

/// <summary>
/// Writes out what one person may see, so their data is never locked in: a task file that
/// EtchaSketch can import again (see docs/import.md), a spreadsheet, and a calendar file. Uses the
/// same privacy rules as the screens: other people's private tasks and the notes on their shared
/// ones are never included.
/// </summary>
public static class TaskExport
{
    /// <summary>The source an exported task carries, so importing it again brings back the same task rather than a copy.</summary>
    public const string SourcePrefix = "etchasketch:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Document(string Format, int Version, string ExportedAt, string ExportedBy, IReadOnlyList<ImportedTask> Tasks);

    /// <summary>Every task this person may see, open and finished, as an import file.</summary>
    public static string ToJson(TaskRepository tasks, Profile person, DateTimeOffset now) =>
        JsonSerializer.Serialize(
            new Document(TaskImport.Format, 1, now.ToString("O", CultureInfo.InvariantCulture), person.DisplayName,
                tasks.ExportableBy(person.Id).Select(task => ToImported(task, tasks, person.Id)).ToList()),
            Json);

    /// <summary>Every task this person may see, as a spreadsheet (comma-separated, one row per task).</summary>
    public static string ToCsv(TaskRepository tasks, Household household, Profile person)
    {
        var names = household.Members.ToDictionary(member => member.Id, member => member.DisplayName);
        var csv = new StringBuilder();
        Row(csv, "Title", "Done", "Due", "Time", "Priority", "Project", "Labels", "Repeats", "Shared", "Who's doing it", "Notes");
        foreach (var task in tasks.ExportableBy(person.Id))
        {
            Row(csv,
                task.Title,
                task.CompletedAt is { } done ? done.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                task.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                task.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                task.Priority.ToString(),
                task.Project ?? "",
                string.Join("; ", task.Labels),
                task.Recurrence is { } repeat ? RepeatText(repeat) : "",
                task.Tier <= Core.Privacy.PrivacyTier.Household ? "yes" : "no",
                task.AssigneeId is { } assignee ? names.GetValueOrDefault(assignee, "") : "",
                task.Notes ?? "");
        }

        return csv.ToString();
    }

    /// <summary>
    /// This person's open dated tasks as calendar events, for any calendar app: all-day on the due date,
    /// or a short event at the due time, repeating like the task and with its reminders.
    /// </summary>
    public static string ToIcs(TaskRepository tasks, Profile person, DateTimeOffset now)
    {
        var ics = new StringBuilder();
        Line(ics, "BEGIN:VCALENDAR");
        Line(ics, "VERSION:2.0");
        Line(ics, "PRODID:-//EtchaSketch//EN");
        Line(ics, "CALSCALE:GREGORIAN");
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var task in tasks.ExportableBy(person.Id).Where(task => task.CompletedAt is null && task.Due is not null))
        {
            var due = task.Due!.Value;
            Line(ics, "BEGIN:VEVENT");
            Line(ics, $"UID:{task.Id}@etchasketch");
            Line(ics, $"DTSTAMP:{stamp}");
            if (task.DueAt is { } at)
            {
                // No time zone: "floating" time, so 14:05 is 14:05 wherever the calendar is.
                Line(ics, $"DTSTART:{at.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}");
                Line(ics, $"DTEND:{at.AddMinutes(15).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}");
            }
            else
            {
                Line(ics, $"DTSTART;VALUE=DATE:{due.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
                Line(ics, $"DTEND;VALUE=DATE:{due.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
            }

            Line(ics, $"SUMMARY:{Escape(task.Title)}");
            if (!string.IsNullOrEmpty(task.Notes))
            {
                Line(ics, $"DESCRIPTION:{Escape(task.Notes)}");
            }

            if (task.Recurrence is { } repeat)
            {
                Line(ics, $"RRULE:FREQ={repeat.Frequency.ToString().ToUpperInvariant()};INTERVAL={repeat.Interval}");
            }

            if (task.RemindMinutesBefore is { } minutes)
            {
                Alarm(ics, $"-PT{minutes}M", task.Title);
            }

            if (task.RemindDaysBefore is { } days)
            {
                Alarm(ics, $"-P{days}D", task.Title);
            }

            Line(ics, "END:VEVENT");
        }

        Line(ics, "END:VCALENDAR");
        return ics.ToString();
    }

    // A link is written once, on the task it starts from; the import makes it from both sides.
    private static ImportedTask ToImported(TaskItem task, TaskRepository tasks, Guid personId) => new()
    {
        Parent = task.ParentId is { } parent ? SourcePrefix + parent : null,
        Links = tasks.LinksOf(task.Id, personId) is var links && links.Any(link => !link.OtherIsFirst)
            ? links.Where(link => !link.OtherIsFirst)
                .Select(link => new ImportedLink(link.Kind == TaskLinkKind.Blocks ? "blocks" : "related", SourcePrefix + link.Other.Id))
                .ToList()
            : null,
        Title = task.Title,
        Notes = task.Notes,
        Due = task.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DueTime = task.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
        Priority = task.Priority.ToString().ToLowerInvariant(),
        Repeat = task.Recurrence is { } repeat ? new ImportedRepeat(repeat.Interval, repeat.Frequency.ToString().ToLowerInvariant()) : null,
        Shared = task.Tier <= Core.Privacy.PrivacyTier.Household,
        Done = task.CompletedAt is not null,
        Source = SourcePrefix + task.Id,
        Project = task.Project,
        Labels = task.Labels.Count == 0 ? null : task.Labels,
        RemindDaysBefore = task.RemindDaysBefore,
        RemindMinutesBefore = task.RemindMinutesBefore,
        Alarm = task.Alarm ? true : null,
    };

    private static string RepeatText(Recurrence repeat) => (repeat.Frequency, repeat.Interval) switch
    {
        (RecurrenceFrequency.Daily, 1) => "daily",
        (RecurrenceFrequency.Weekly, 1) => "weekly",
        (RecurrenceFrequency.Monthly, 1) => "monthly",
        (var frequency, var n) => $"every {n} {frequency switch { RecurrenceFrequency.Daily => "days", RecurrenceFrequency.Weekly => "weeks", _ => "months" }}",
    };

    private static void Row(StringBuilder csv, params string[] cells)
    {
        csv.Append(string.Join(",", cells.Select(Cell)));
        csv.Append("\r\n");
    }

    // Quoted where needed, and a leading = + - @ is defused so a spreadsheet shows it rather than running it.
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static void Alarm(StringBuilder ics, string trigger, string title)
    {
        Line(ics, "BEGIN:VALARM");
        Line(ics, "ACTION:DISPLAY");
        Line(ics, $"DESCRIPTION:{Escape(title)}");
        Line(ics, $"TRIGGER:{trigger}");
        Line(ics, "END:VALARM");
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    // Calendar lines are at most 75 bytes; longer ones continue on the next line after a space.
    private static void Line(StringBuilder ics, string line)
    {
        int start = 0, bytes = 0, limit = 75, i = 0;
        while (i < line.Length)
        {
            Rune.DecodeFromUtf16(line.AsSpan(i), out var rune, out var used);
            if (bytes + rune.Utf8SequenceLength > limit)
            {
                ics.Append(line, start, i - start).Append("\r\n ");
                start = i;
                bytes = 0;
                limit = 74;
            }

            bytes += rune.Utf8SequenceLength;
            i += used;
        }

        ics.Append(line, start, line.Length - start).Append("\r\n");
    }
}
