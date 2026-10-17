using System.Security.Cryptography;

namespace EtchaSketch.Data.Sync;

/// <summary>
/// AES-256-GCM with a fresh random nonce per message, laid out as nonce, ciphertext, tag. The
/// associated data ties a message to where it belongs, so it can't be replayed somewhere else.
/// </summary>
internal static class SealedBox
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceBytes, plaintext.Length), output.AsSpan(NonceBytes + plaintext.Length), associatedData);
        return output;
    }

    /// <exception cref="CryptographicException">The message was changed, or sealed with another key or for somewhere else.</exception>
    public static byte[] Open(byte[] key, byte[] message, byte[] associatedData)
    {
        if (message.Length < NonceBytes + TagBytes)
        {
            throw new AuthenticationTagMismatchException();
        }

        var plaintext = new byte[message.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(message.AsSpan(0, NonceBytes), message.AsSpan(NonceBytes, plaintext.Length), message.AsSpan(^TagBytes), plaintext, associatedData);
        return plaintext;
    }

    /// <summary>A subkey for one purpose, so one secret never does two jobs.</summary>
    public static byte[] Derive(byte[] secret, string purpose, int length = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, length, salt: null, info: System.Text.Encoding.UTF8.GetBytes(purpose));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
