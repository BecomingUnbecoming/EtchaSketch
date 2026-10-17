namespace EtchaSketch.Tests.TestSupport;

/// <summary>A clock stopped at one moment, in UTC, so screens show the same dates and times every run.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
