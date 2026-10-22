using EtchaSketch.Core.Calendar;

namespace EtchaSketch.Tests.Calendar;

public sealed class IcsTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static IcsEvent One(string vevent, TimeZoneInfo? zone = null) =>
        Assert.Single(Ics.Parse($"BEGIN:VCALENDAR\r\nVERSION:2.0\r\n{vevent}\r\nEND:VCALENDAR\r\n", zone ?? London));

    private static List<DateOnly> Days(IcsEvent ics, string from, string to) =>
        Ics.Starts(ics, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)).ToList();

    [Fact]
    public void A_holiday_list_reads_as_all_day_events()
    {
        var events = Ics.Parse("""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:xmas@example
            DTSTART;VALUE=DATE:20261225
            DTEND;VALUE=DATE:20261226
            SUMMARY:Christmas Day
            END:VEVENT
            BEGIN:VEVENT
            UID:half-term@example
            DTSTART;VALUE=DATE:20261026
            DTEND;VALUE=DATE:20261031
            SUMMARY:Half term
            END:VEVENT
            END:VCALENDAR
            """, London);

        Assert.Equal(["Christmas Day", "Half term"], events.Select(e => e.Summary));
        Assert.All(events, e => Assert.True(e.IsAllDay));
        Assert.Equal((0, 4), (events[0].ExtraDays, events[1].ExtraDays));
    }

    [Fact]
    public void Folded_lines_escapes_and_an_alarms_own_text_are_handled()
    {
        var ics = One("""
            BEGIN:VEVENT
            UID:1
            DTSTART:20261021T090000
            SUMMARY:Parents' evening\, Year 4\; bring the
              forms
            DESCRIPTION:Line one\nLine two \\ done
            LOCATION:School hall
            BEGIN:VALARM
            ACTION:DISPLAY
            DESCRIPTION:Reminder text
            END:VALARM
            END:VEVENT
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal("Parents' evening, Year 4; bring the forms", ics.Summary);
        Assert.Equal("Line one\nLine two \\ done", ics.Description);
        Assert.Equal("School hall", ics.Location);
        Assert.Equal(new IcsTime(new DateOnly(2026, 10, 21), new TimeOnly(9, 0)), ics.Start);
    }

    [Fact]
    public void Times_with_a_zone_or_in_utc_become_wall_clock_time_here()
    {
        var newYork = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;TZID=America/New_York:20261021T090000\r\nDTEND;TZID=America/New_York:20261021T100000\r\nSUMMARY:Call\r\nEND:VEVENT");
        Assert.Equal((new TimeOnly(14, 0), new TimeOnly(15, 0)), (newYork.Start.Time, newYork.End!.Value.Time));

        // After the clocks go back in London (25 Oct) but before New York's (1 Nov), the gap is 4 hours.
        var later = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;TZID=America/New_York:20261028T090000\r\nSUMMARY:Call\r\nEND:VEVENT");
        Assert.Equal(new TimeOnly(13, 0), later.Start.Time);

        var utc = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART:20260701T120000Z\r\nDURATION:PT1H30M\r\nSUMMARY:Webinar\r\nEND:VEVENT");
        Assert.Equal((new TimeOnly(13, 0), new TimeOnly(14, 30)), (utc.Start.Time, utc.End!.Value.Time));
    }

    [Fact]
    public void A_weekly_repeat_on_two_days_stops_at_its_count_and_skips_excluded_days()
    {
        var ics = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART:20261005T180000\r\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6\r\nEXDATE:20261014T180000\r\nSUMMARY:Swimming\r\nEND:VEVENT");

        Assert.Equal([5, 7, 12, 19, 21], Days(ics, "2026-10-01", "2026-12-31").Select(day => day.Day));
    }

    [Fact]
    public void Monthly_and_yearly_rules_find_the_right_weekday()
    {
        var secondTuesday = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20261013\r\nRRULE:FREQ=MONTHLY;BYDAY=2TU\r\nSUMMARY:Book club\r\nEND:VEVENT");
        Assert.Equal(["2026-10-13", "2026-11-10", "2026-12-08"], Days(secondTuesday, "2026-10-01", "2026-12-31").Select(day => day.ToString("yyyy-MM-dd", null)));

        var lastFriday = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20261030\r\nRRULE:FREQ=MONTHLY;BYDAY=-1FR;UNTIL=20261231\r\nSUMMARY:Payday\r\nEND:VEVENT");
        Assert.Equal(["2026-10-30", "2026-11-27", "2026-12-25"], Days(lastFriday, "2026-10-01", "2027-03-31").Select(day => day.ToString("yyyy-MM-dd", null)));

        var thanksgiving = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20261126\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=4TH\r\nSUMMARY:Thanksgiving\r\nEND:VEVENT");
        Assert.Equal(["2026-11-26", "2027-11-25", "2028-11-23"], Days(thanksgiving, "2026-01-01", "2028-12-31").Select(day => day.ToString("yyyy-MM-dd", null)));

        var birthday = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20240229\r\nRRULE:FREQ=YEARLY\r\nSUMMARY:Leap birthday\r\nEND:VEVENT");
        Assert.Equal(["2024-02-29", "2028-02-29"], Days(birthday, "2024-01-01", "2028-12-31").Select(day => day.ToString("yyyy-MM-dd", null)));

        var everyOtherDay = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20261001\r\nRRULE:FREQ=DAILY;INTERVAL=2;COUNT=3\r\nSUMMARY:Water\r\nEND:VEVENT");
        Assert.Equal([1, 3, 5], Days(everyOtherDay, "2026-10-01", "2026-10-31").Select(day => day.Day));
    }

    [Fact]
    public void An_event_over_several_days_shows_in_a_range_that_starts_after_its_first_day()
    {
        var trip = One("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20261018\r\nDTEND;VALUE=DATE:20261023\r\nSUMMARY:Trip\r\nEND:VEVENT");

        Assert.Equal([new DateOnly(2026, 10, 18)], Days(trip, "2026-10-20", "2026-10-26"));
    }

    [Fact]
    public void Broken_or_unknown_parts_are_skipped_not_fatal()
    {
        var events = Ics.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:No start\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART:20261021\r\nRRULE:FREQ=SECONDLY\r\nEND:VEVENT\r\nnot a line\r\nEND:VCALENDAR", London);

        var only = Assert.Single(events);
        Assert.Equal("(no title)", only.Summary);
        Assert.Equal([new DateOnly(2026, 10, 21)], Days(only, "2026-10-01", "2026-10-31"));
    }
}
