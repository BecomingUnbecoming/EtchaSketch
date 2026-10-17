using System.Security.Cryptography;
using System.Text;

namespace EtchaSketch.Server;

/// <summary>
/// Who may start things on this server. With a join token set, only devices that know it can
/// create a stream or leave a new-device code; everyone else is turned away. Devices that already
/// belong keep working with their own stream keys. With no token set the server is open to anyone
/// who can reach it, which is only sensible on a home network.
/// </summary>
public sealed class SyncAccess(string? joinToken)
{
    public const string Header = "X-Join-Token";
    public const string Challenge = "JoinToken";

    private readonly byte[]? _expected = string.IsNullOrEmpty(joinToken) ? null : Hash(joinToken);

    public bool IsOpen => _expected is null;

    public bool Allows(HttpRequest request) =>
        _expected is null || CryptographicOperations.FixedTimeEquals(_expected, Hash(request.Headers[Header].ToString()));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
