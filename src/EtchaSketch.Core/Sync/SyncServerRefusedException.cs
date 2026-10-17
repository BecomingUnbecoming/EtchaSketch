namespace EtchaSketch.Core.Sync;

/// <summary>The sync server turned a request down because of a limit it keeps (too many requests, or no room left), not because of a key.</summary>
public sealed class SyncServerRefusedException(string message) : Exception(message);
