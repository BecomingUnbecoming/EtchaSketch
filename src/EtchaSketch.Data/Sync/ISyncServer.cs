using EtchaSketch.Core.Sync;

namespace EtchaSketch.Data.Sync;

/// <summary>The sync server as a device sees it: a place to leave and fetch sealed bytes.</summary>
public interface ISyncServer
{
    /// <summary>The server's join token, sent with every request. Servers that don't ask for one ignore it.</summary>
    string? JoinToken { get; set; }

    /// <exception cref="SyncAccessDeniedException">The stream belongs to another key.</exception>
    Task PushAsync(string streamId, string accessToken, IReadOnlyList<byte[]> changes, CancellationToken cancellationToken = default);

    /// <exception cref="SyncAccessDeniedException">The stream belongs to another key.</exception>
    Task<PullResponse> PullAsync(string streamId, string accessToken, long after, CancellationToken cancellationToken = default);

    Task PutPairingAsync(string pairingId, byte[] sealedKeys, CancellationToken cancellationToken = default);

    /// <summary>Takes the sealed keys left under a pairing id, or returns null when there are none.</summary>
    Task<byte[]?> ClaimPairingAsync(string pairingId, CancellationToken cancellationToken = default);

    /// <summary>Whether the server knows about devices' own tokens. Older servers don't.</summary>
    Task<bool> SupportsDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gives this device its own token for a stream, using a token that already works there or a one-time pass.</summary>
    Task<DeviceOutcome> RegisterDeviceAsync(string streamId, string authToken, string deviceId, string deviceToken, CancellationToken cancellationToken = default);

    /// <summary>Leaves a one-time pass a new device can register with.</summary>
    Task<DeviceOutcome> AddEnrolmentAsync(string streamId, string authToken, string enrolmentToken, CancellationToken cancellationToken = default);

    /// <summary>Cuts a device off a stream.</summary>
    Task<DeviceOutcome> RemoveDeviceAsync(string streamId, string authToken, string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>What the server said to a request about a stream's devices.</summary>
public enum DeviceOutcome
{
    Done,
    Denied,
    UnknownStream,
    LastDevice,
    Full,
}
