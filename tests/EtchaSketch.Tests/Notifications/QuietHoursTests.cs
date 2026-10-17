using EtchaSketch.Core.Notifications;

namespace EtchaSketch.Tests.Notifications;

public class QuietHoursTests
{
    private static readonly QuietHours Overnight = new(new TimeOnly(22, 0), new TimeOnly(7, 0));

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(6, 59, true)]
    [InlineData(22, 0, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    public void Overnight_quiet_hours_cover_the_night(int hour, int minute, bool quiet)
    {
        Assert.Equal(quiet, Overnight.Contains(new TimeOnly(hour, minute)));
    }

    [Fact]
    public void Daytime_quiet_hours_cover_only_that_stretch()
    {
        var nap = new QuietHours(new TimeOnly(13, 0), new TimeOnly(14, 0));

        Assert.True(nap.Contains(new TimeOnly(13, 30)));
        Assert.False(nap.Contains(new TimeOnly(14, 0)));
        Assert.False(nap.Contains(new TimeOnly(23, 0)));
    }

    [Fact]
    public void The_same_start_and_end_is_never_quiet()
    {
        var none = new QuietHours(new TimeOnly(9, 0), new TimeOnly(9, 0));

        Assert.False(none.Contains(new TimeOnly(9, 0)));
        Assert.Equal(At(21, 9), none.DeliveryTime(At(21, 9)));
    }

    [Fact]
    public void Anything_arriving_during_quiet_hours_waits_until_they_end()
    {
        Assert.Equal(At(22, 7), Overnight.DeliveryTime(At(21, 23, 30)));
        Assert.Equal(At(22, 7), Overnight.DeliveryTime(At(22, 2)));
        Assert.Equal(At(21, 12), Overnight.DeliveryTime(At(21, 12)));
    }
}
