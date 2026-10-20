namespace EtchaSketch.Server;

/// <summary>
/// How much the sync server will take from anyone who can reach it. Sync is end to end encrypted,
/// but the server can't tell your data from junk, so these keep a stranger (or a bug) on the
/// network from filling the disk. Every value can be changed in the "Sync" configuration section,
/// for example with the environment variable <c>Sync__MaxStreams</c>.
/// </summary>
public sealed record SyncLimits
{
    /// <summary>The biggest request the server reads at all.</summary>
    public long MaxRequestBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>The biggest single sealed change.</summary>
    public int MaxChangeBytes { get; init; } = 256 * 1024;

    /// <summary>The most changes one push may carry. Devices send 200 at a time.</summary>
    public int MaxChangesPerPush { get; init; } = 500;

    /// <summary>How many streams may exist: one for the household and one per person, so a household of five needs six.</summary>
    public int MaxStreams { get; init; } = 20;

    /// <summary>How much one stream may store.</summary>
    public long MaxStreamBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>How many new-device codes may wait to be claimed at once. Each lasts 15 minutes.</summary>
    public int MaxPendingPairings { get; init; } = 20;

    /// <summary>How many devices may have their own token for one stream.</summary>
    public int MaxDevicesPerStream { get; init; } = 50;

    /// <summary>The most requests one address may make a minute.</summary>
    public int RequestsPerMinute { get; init; } = 600;

    /// <summary>The most new-device requests one address may make a minute.</summary>
    public int PairingRequestsPerMinute { get; init; } = 30;
}
