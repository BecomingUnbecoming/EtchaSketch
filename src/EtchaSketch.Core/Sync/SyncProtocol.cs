namespace EtchaSketch.Core.Sync;

// What devices and the sync server send each other. Every change is sealed on the device before it
// leaves, so the server only ever handles opaque bytes. byte[] travels as base64 in JSON.

/// <summary>Changes for one stream, in the order they should be applied.</summary>
public sealed record PushRequest(IReadOnlyList<byte[]> Changes);

/// <summary>One stored change and its position in the server's log.</summary>
public sealed record StoredChange(long Seq, byte[] Data);

/// <summary>A page of changes after a position; <paramref name="More"/> says another page is waiting.</summary>
public sealed record PullResponse(IReadOnlyList<StoredChange> Changes, bool More);

/// <summary>A device's own token for a stream, or a one-time pass a new device can register with.</summary>
public sealed record TokenRequest(string Token);

/// <summary>What this server can do, so a newer app knows what an older server lacks.</summary>
public sealed record ServerFeatures(bool Devices);

/// <summary>A sealed bundle of keys waiting for a new device to claim it with its pairing code.</summary>
public sealed record PairingRequest(byte[] Data);
