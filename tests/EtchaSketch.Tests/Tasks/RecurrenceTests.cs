using EtchaSketch.Core.Tasks;

namespace EtchaSketch.Tests.Tasks;

public class RecurrenceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);

    [Theory]
    [InlineData(RecurrenceFrequency.Daily, 1, "2026-10-20")]
    [InlineData(RecurrenceFrequency.Daily, 3, "2026-10-22")]
    [InlineData(RecurrenceFrequency.Weekly, 1, "2026-10-26")]
    [InlineData(RecurrenceFrequency.Weekly, 2, "2026-11-02")]
    [InlineData(RecurrenceFrequency.Monthly, 1, "2026-11-19")]
    public void Steps_forward_by_its_interval(RecurrenceFrequency frequency, int interval, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), new Recurrence(frequency, interval).After(Monday));
    }

    [Fact]
    public void Monthly_on_the_31st_lands_on_the_last_day_of_shorter_months()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), new Recurrence(RecurrenceFrequency.Monthly).After(new DateOnly(2027, 1, 31)));
    }

    [Fact]
    public void A_late_completion_skips_the_occurrences_that_were_missed()
    {
        var weekly = new Recurrence(RecurrenceFrequency.Weekly);

        // Due Monday the 19th, done the following Wednesday: next is Monday the 2nd, not the 26th.
        Assert.Equal(new DateOnly(2026, 11, 2), weekly.NextAfter(Monday, completedOn: new DateOnly(2026, 10, 28)));
    }

    [Fact]
    public void An_early_completion_moves_to_the_next_occurrence()
    {
        Assert.Equal(new DateOnly(2026, 10, 26),
            new Recurrence(RecurrenceFrequency.Weekly).NextAfter(Monday, completedOn: new DateOnly(2026, 10, 17)));
    }

    [Fact]
    public void Interval_must_be_at_least_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Recurrence(RecurrenceFrequency.Daily, 0));
    }
}
