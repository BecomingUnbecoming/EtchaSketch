using System.Text;

namespace EtchaSketch.Data.Sync;

/// <summary>
/// The secret for one sync stream: a household's shared items, or one person's private ones.
/// Everything the server sees is derived from it one way, so the server learns neither the key
/// nor whose stream it is.
/// </summary>
public sealed class StreamKey
{
    private readonly byte[] _encryptionKey;
    private readonly byte[] _associatedData;

    private StreamKey(byte[] key)
    {
        _encryptionKey = SealedBox.Derive(key, "etchasketch sync encryption");
        StreamId = SealedBox.Base64Url(SealedBox.Derive(key, "etchasketch sync stream id", 16));
        AccessToken = SealedBox.Base64Url(SealedBox.Derive(key, "etchasketch sync access token"));
        _associatedData = Encoding.UTF8.GetBytes(StreamId);
    }

    /// <summary>What the server files this stream under.</summary>
    public string StreamId { get; }

    /// <summary>Proves to the server that a device holds the key, without giving the key away.</summary>
    public string AccessToken { get; }

    public static StreamKey From(byte[] key) =>
        key.Length == 32 ? new StreamKey(key) : throw new ArgumentException("A stream key is 32 bytes.", nameof(key));

    public byte[] Seal(byte[] plaintext) => SealedBox.Seal(_encryptionKey, plaintext, _associatedData);

    public byte[] Open(byte[] message) => SealedBox.Open(_encryptionKey, message, _associatedData);
}
