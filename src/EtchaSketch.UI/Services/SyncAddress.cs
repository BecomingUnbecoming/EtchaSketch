using System;

namespace EtchaSketch.UI.Services;

internal static class SyncAddress
{
    /// <summary>Reads a sync server address as people type it, adding http:// when it's left off.</summary>
    /// <exception cref="FormatException">It isn't a web address.</exception>
    public static Uri Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(address.Host)
            || !string.IsNullOrEmpty(address.Query))
        {
            throw new FormatException("Enter the sync server's address, like http://192.168.1.20:5080.");
        }

        // Requests are relative to the address, so it has to end in a slash.
        return address.AbsolutePath.EndsWith('/') ? address : new Uri(address + "/");
    }
}
