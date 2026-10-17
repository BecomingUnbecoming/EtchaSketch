namespace EtchaSketch.Data.Sync;

/// <summary>The code was already used, has expired, or was never issued.</summary>
public sealed class PairingCodeNotFoundException()
    : Exception("That pairing code has already been used or has expired. Make a new one on a device that's already set up.");
