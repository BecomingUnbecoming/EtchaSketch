namespace EtchaSketch.Core.Sync;

/// <summary>The server holds this stream for a different key, so this device may not read or write it.</summary>
public sealed class SyncAccessDeniedException(string streamId)
    : Exception($"The sync server refused access to stream {streamId}.");
