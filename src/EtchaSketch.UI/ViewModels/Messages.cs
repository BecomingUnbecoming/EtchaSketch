using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using EtchaSketch.Core.Sync;
using EtchaSketch.Data.Sync;

namespace EtchaSketch.UI.ViewModels;

internal static class Messages
{
    /// <summary>The message from a validation error, without .NET's "(Parameter 'x')" suffix.</summary>
    public static string Friendly(ArgumentException exception) =>
        exception.ParamName is null
            ? exception.Message
            : exception.Message.Replace($" (Parameter '{exception.ParamName}')", "", StringComparison.Ordinal);

    /// <summary>Whether a sync failed for a reason worth telling the person, rather than a bug.</summary>
    public static bool IsSyncProblem(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or FormatException or PairingCodeNotFoundException
            or SyncAccessDeniedException or CryptographicException or InvalidDataException or InvalidOperationException
            or ArgumentException;

    public static string ForSync(Exception exception, Uri? address) => exception switch
    {
        HttpRequestException or TaskCanceledException =>
            $"Couldn't reach the sync server at {address}. Check the address, and that this device is on your home network or VPN.",
        SyncAccessDeniedException =>
            "The sync server refused this device's key. Pair it again with a new code from a device that's already set up.",
        CryptographicException =>
            "Something from the sync server didn't match this household's keys, so it was ignored and nothing changed.",
        ArgumentException argument => Friendly(argument),
        _ => exception.Message,
    };
}
