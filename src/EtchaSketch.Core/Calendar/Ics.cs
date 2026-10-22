using System.Globalization;
using System.Text;

namespace EtchaSketch.Core.Calendar;

/// <summary>A date and maybe a time, already in this device's wall-clock time.</summary>
public readonly record struct IcsTime(DateOnly Date, TimeOnly? Time);

/// <summary>A repeat rule (RRULE), the parts EtchaSketch understands.</summary>
public sealed record IcsRepeat(string Frequency, int Interval, int? Count, DateOnly? Until, IReadOnlyList<(int Ordinal, DayOfWeek Day)> ByDay,
    IReadOnlyList<int> ByMonthDay, IReadOnlyList<int> ByMonth);

/// <summary>One VEVENT from a calendar file.</summary>
public sealed record IcsEvent(
    string Uid,
    string Summary,
    IcsTime Start,
    IcsTime? End,
    string? Location,
    string? Description,
    IcsRepeat? Repeat,
    IReadOnlyList<DateOnly> Excluded,
    DateOnly? RecurrenceId)
{
    public bool IsAllDay => Start.Time is null;

    /// <summary>How many days it lasts. An all-day event's DTEND is the day after its last day.</summary>
    public int ExtraDays => End is not { } end ? 0
        : IsAllDay ? Math.Max(0, end.Date.DayNumber - Start.Date.DayNumber - 1)
        : Math.Max(0, end.Date.DayNumber - Start.Date.DayNumber - (end.Time == TimeOnly.MinValue ? 1 : 0));
}

/// <summary>
/// Reads iCalendar (.ics) files, the kind calendar apps, schools and holiday lists publish.
/// Times with a time zone or in UTC are turned into this device's wall-clock time.
/// </summary>
public static class Ics
{
    public static IReadOnlyList<IcsEvent> Parse(string text, TimeZoneInfo? local = null)
    {
        local ??= TimeZoneInfo.Local;
        var events = new List<IcsEvent>();
        Dictionary<string, (string Value, Dictionary<string, string> Parameters)>? current = null;
        var excluded = new List<DateOnly>();
        var depth = 0;
        foreach (var line in Unfold(text))
        {
            var (name, parameters, value) = Split(line);
            if (name == "BEGIN" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                excluded = [];
                depth = 0;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            // Alarms and other parts inside an event have their own SUMMARY and so on; skip them.
            if (name == "BEGIN")
            {
                depth++;
                continue;
            }

            if (name == "END" && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (name == "END" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (Build(current, excluded, local) is { } built)
                {
                    events.Add(built);
                }

                current = null;
                continue;
            }

            if (name == "EXDATE")
            {
                foreach (var part in value.Split(','))
                {
                    if (Time(part, parameters, local) is { } time)
                    {
                        excluded.Add(time.Date);
                    }
                }

                continue;
            }

            current.TryAdd(name, (value, parameters));
        }

        return events;
    }

    private static IcsEvent? Build(Dictionary<string, (string Value, Dictionary<string, string> Parameters)> fields, List<DateOnly> excluded, TimeZoneInfo local)
    {
        if (!fields.TryGetValue("DTSTART", out var startField) || Time(startField.Value, startField.Parameters, local) is not { } start)
        {
            return null;
        }

        IcsTime? end = fields.TryGetValue("DTEND", out var endField) ? Time(endField.Value, endField.Parameters, local) : null;
        if (end is null && fields.TryGetValue("DURATION", out var duration) && Duration(duration.Value) is { } length)
        {
            var from = start.Date.ToDateTime(start.Time ?? TimeOnly.MinValue) + length;
            end = new IcsTime(DateOnly.FromDateTime(from), start.Time is null ? null : TimeOnly.FromDateTime(from));
        }

        DateOnly? recurrenceId = fields.TryGetValue("RECURRENCE-ID", out var rid) ? Time(rid.Value, rid.Parameters, local)?.Date : null;
        var summary = fields.TryGetValue("SUMMARY", out var s) ? Text(s.Value) : "";
        return new IcsEvent(
            fields.TryGetValue("UID", out var uid) ? uid.Value : Guid.NewGuid().ToString(),
            summary.Length == 0 ? "(no title)" : summary,
            start,
            end,
            fields.TryGetValue("LOCATION", out var location) && Text(location.Value) is { Length: > 0 } where ? where : null,
            fields.TryGetValue("DESCRIPTION", out var description) && Text(description.Value) is { Length: > 0 } about ? about : null,
            fields.TryGetValue("RRULE", out var rule) ? Repeat(rule.Value, local) : null,
            excluded,
            recurrenceId);
    }

    /// <summary>Joins folded lines (a line starting with a space or tab continues the one before).</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t')
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(raw.TrimEnd('\r'));
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static (string Name, Dictionary<string, string> Parameters, string Value) Split(string line)
    {
        // The value starts after the first colon that isn't inside a quoted parameter.
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon < 0)
        {
            return ("", [], "");
        }

        var head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in head.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
            {
                parameters[part[..equals]] = part[(equals + 1)..].Trim('"');
            }
        }

        return (head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    /// <summary>A DATE or DATE-TIME value, in this device's wall-clock time.</summary>
    public static IcsTime? Time(string value, IReadOnlyDictionary<string, string> parameters, TimeZoneInfo local)
    {
        value = value.Trim();
        if (value.Length == 8 || parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase))
        {
            return DateOnly.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new IcsTime(day, null)
                : null;
        }

        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return null;
        }

        DateTime wall;
        if (utc)
        {
            wall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(stamp, DateTimeKind.Utc), local);
        }
        else if (parameters.TryGetValue("TZID", out var zoneId) && Zone(zoneId) is { } zone && zone.Id != local.Id)
        {
            wall = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(stamp, DateTimeKind.Unspecified), zone, local);
        }
        else
        {
            // No zone: "floating" time, the same on every clock.
            wall = stamp;
        }

        return new IcsTime(DateOnly.FromDateTime(wall), TimeOnly.FromDateTime(wall));
    }

    private static TimeZoneInfo? Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Undoes iCalendar text escaping: a backslash before n is a line break, before anything else that character.</summary>
    public static string Text(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                text.Append(value[i] is 'n' or 'N' ? '\n' : value[i]);
            }
            else
            {
                text.Append(value[i]);
            }
        }

        return text.ToString().Trim();
    }

    // A DURATION like P1D, PT1H30M or P2W.
    private static TimeSpan? Duration(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(),
            @"^\+?P(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$");
        if (!match.Success)
        {
            return null;
        }

        int Part(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(Part("w") * 7 + Part("d"), Part("h"), Part("m"), Part("s"));
    }

    private static IcsRepeat? Repeat(string rule, TimeZoneInfo local)
    {
        var parts = rule.Split(';').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1], StringComparer.Ordinal);
        if (!parts.TryGetValue("FREQ", out var frequency))
        {
            return null;
        }

        var byDay = new List<(int, DayOfWeek)>();
        foreach (var day in parts.GetValueOrDefault("BYDAY", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = day[^2..].ToUpperInvariant();
            var ordinal = day.Length > 2 && int.TryParse(day[..^2], CultureInfo.InvariantCulture, out var n) ? n : 0;
            DayOfWeek? weekday = code switch
            {
                "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
            };
            if (weekday is { } found)
            {
                byDay.Add((ordinal, found));
            }
        }

        return new IcsRepeat(
            frequency.ToUpperInvariant(),
            int.TryParse(parts.GetValueOrDefault("INTERVAL"), CultureInfo.InvariantCulture, out var interval) && interval > 0 ? interval : 1,
            int.TryParse(parts.GetValueOrDefault("COUNT"), CultureInfo.InvariantCulture, out var count) ? count : null,
            parts.TryGetValue("UNTIL", out var until) ? Time(until, new Dictionary<string, string>(), local)?.Date : null,
            byDay,
            Numbers(parts.GetValueOrDefault("BYMONTHDAY")),
            Numbers(parts.GetValueOrDefault("BYMONTH")));
    }

    private static List<int> Numbers(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part => int.TryParse(part, CultureInfo.InvariantCulture, out var n) ? n : 0).Where(n => n != 0).ToList();

    /// <summary>
    /// The days an event starts on between two days: its start, and each repeat. A repeat rule
    /// beyond what's understood here shows only its first day.
    /// </summary>
    public static IEnumerable<DateOnly> Starts(IcsEvent ics, DateOnly from, DateOnly to)
    {
        var excluded = ics.Excluded.ToHashSet();
        var earliest = from.AddDays(-ics.ExtraDays);
        if (ics.Repeat is not { } repeat)
        {
            if (ics.Start.Date >= earliest && ics.Start.Date <= to)
            {
                yield return ics.Start.Date;
            }

            yield break;
        }

        var produced = 0;
        foreach (var day in Candidates(ics.Start.Date, repeat))
        {
            if (day > to || repeat.Until is { } until && day > until || repeat.Count is { } count && produced >= count)
            {
                yield break;
            }

            produced++;
            if (day >= earliest && !excluded.Contains(day))
            {
                yield return day;
            }
        }
    }

    // Every day the rule produces from the start, in order, the start first.
    private static IEnumerable<DateOnly> Candidates(DateOnly start, IcsRepeat repeat)
    {
        const int Limit = 5_000;
        switch (repeat.Frequency)
        {
            case "DAILY":
                for (var i = 0; i < Limit; i++)
                {
                    yield return start.AddDays(i * repeat.Interval);
                }

                yield break;

            case "WEEKLY":
                var days = repeat.ByDay.Count == 0 ? [start.DayOfWeek] : repeat.ByDay.Select(pair => pair.Day).Distinct().ToList();
                // Weeks start on Monday (the iCalendar default).
                var weekStart = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var week = 0; week < Limit; week++)
                {
                    var monday = weekStart.AddDays(7 * week * repeat.Interval);
                    foreach (var day in days.Select(d => monday.AddDays(((int)d + 6) % 7)).Order())
                    {
                        if (day >= start)
                        {
                            yield return day;
                        }
                    }
                }

                yield break;

            case "MONTHLY":
                for (var month = 0; month < Limit; month++)
                {
                    var first = new DateOnly(start.Year, start.Month, 1).AddMonths(month * repeat.Interval);
                    foreach (var day in InMonth(first, repeat, start.Day).Order())
                    {
                        if (day >= start)
                        {
                            yield return day;
                        }
                    }
                }

                yield break;

            case "YEARLY":
                for (var year = 0; year < Limit / 12; year++)
                {
                    var months = repeat.ByMonth.Count > 0 ? repeat.ByMonth : [start.Month];
                    foreach (var month in months.Order())
                    {
                        var first = new DateOnly(start.Year + year * repeat.Interval, month, 1);
                        var found = repeat.ByDay.Count > 0 || repeat.ByMonthDay.Count > 0
                            ? InMonth(first, repeat, start.Day)
                            : start.Day <= DateTime.DaysInMonth(first.Year, month) ? [first.AddDays(start.Day - 1)] : [];
                        foreach (var day in found.Order())
                        {
                            if (day >= start)
                            {
                                yield return day;
                            }
                        }
                    }
                }

                yield break;

            default:
                yield return start;
                yield break;
        }
    }

    // The days in one month a rule picks: by month day (negative counts from the end), by weekday
    // with an ordinal ("2TU", "-1FR"), every such weekday, or the start's own day of the month.
    private static List<DateOnly> InMonth(DateOnly first, IcsRepeat repeat, int startDay)
    {
        var length = DateTime.DaysInMonth(first.Year, first.Month);
        var found = new List<DateOnly>();
        foreach (var monthDay in repeat.ByMonthDay)
        {
            var day = monthDay > 0 ? monthDay : length + monthDay + 1;
            if (day >= 1 && day <= length)
            {
                found.Add(first.AddDays(day - 1));
            }
        }

        foreach (var (ordinal, weekday) in repeat.ByDay)
        {
            var matching = Enumerable.Range(0, length).Select(first.AddDays).Where(day => day.DayOfWeek == weekday).ToList();
            if (ordinal == 0)
            {
                found.AddRange(matching);
            }
            else if (Math.Abs(ordinal) <= matching.Count)
            {
                found.Add(ordinal > 0 ? matching[ordinal - 1] : matching[^-ordinal]);
            }
        }

        if (repeat.ByMonthDay.Count == 0 && repeat.ByDay.Count == 0 && startDay <= length)
        {
            found.Add(first.AddDays(startDay - 1));
        }

        return found;
    }
}
