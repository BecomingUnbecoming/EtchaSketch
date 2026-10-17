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
}
