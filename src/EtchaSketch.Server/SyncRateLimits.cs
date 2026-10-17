using System.Threading.RateLimiting;

namespace EtchaSketch.Server;

/// <summary>Per-address request limits, so one noisy or hostile device can't crowd out the household.</summary>
public static class SyncRateLimits
{
    public const string General = "general";
    public const string Pairing = "pairing";

    public static IServiceCollection AddSyncRateLimits(this IServiceCollection services, SyncLimits limits) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(General, context => PerAddress(context, limits.RequestsPerMinute));
            options.AddPolicy(Pairing, context => PerAddress(context, limits.PairingRequestsPerMinute));
        });

    private static RateLimitPartition<string> PerAddress(HttpContext context, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
