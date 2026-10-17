using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EtchaSketch.Data.Sync;

/// <summary>What a recovery key opens into: who the device was for, and every sync key it held.</summary>
public sealed record RecoveryContents(Guid ProfileId, IReadOnlyDictionary<string, byte[]> Keys);

/// <summary>
/// The sync keys, locked under a passphrase and written as text, so they can be kept somewhere
/// safe (a password manager's note, a printout) and brought back if every device is lost. The
/// server's copy of your data can only be opened with these keys.
/// </summary>
/// <remarks>
/// Layout: a version tag, a random salt and the iteration count, then the keys sealed with a key
/// stretched from the passphrase (PBKDF2-SHA256). The header is bound to the sealed part, so
/// changing any of it makes opening fail. The text holds nothing readable and nothing but keys.
/// </remarks>
public static class RecoveryKey
{
    public const int MinimumPassphraseLength = 12;
    public const int DefaultIterations = 600_000;

    private const string Prefix = "ESKEY1-";
    private const int SaltBytes = 16;
    private const int HeaderBytes = SaltBytes + sizeof(int);

    /// <exception cref="ArgumentException">The passphrase is too short.</exception>
    public static string Create(string passphrase, RecoveryContents contents, int iterations = DefaultIterations)
    {
        if (passphrase is null || passphrase.Trim().Length < MinimumPassphraseLength)
        {
            throw new ArgumentException($"Use a passphrase of at least {MinimumPassphraseLength} characters.", nameof(passphrase));
        }

        var header = new byte[HeaderBytes];
        RandomNumberGenerator.Fill(header.AsSpan(0, SaltBytes));
        BitConverter.TryWriteBytes(header.AsSpan(SaltBytes), iterations);

        var body = JsonSerializer.SerializeToUtf8Bytes(new Body(contents.ProfileId, new Dictionary<string, byte[]>(contents.Keys)));
        var sealedBody = SealedBox.Seal(Stretch(passphrase, header, iterations), body, header);
        return Prefix + SealedBox.Base64Url([.. header, .. sealedBody]);
    }

    /// <exception cref="FormatException">The text isn't a recovery key, or part of it is missing.</exception>
    /// <exception cref="CryptographicException">The passphrase is wrong, or the text was changed.</exception>
    public static RecoveryContents Open(string passphrase, string text)
    {
        var compact = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!compact.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException("That isn't a recovery key. It should start with ESKEY1-.");
        }

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(compact[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new FormatException("That recovery key has a missing or mistyped part. Copy it again in full.");
        }

        if (bytes.Length <= HeaderBytes)
        {
            throw new FormatException("That recovery key is cut short. Copy it again in full.");
        }

        var header = bytes[..HeaderBytes];
        var iterations = BitConverter.ToInt32(header, SaltBytes);
        if (iterations is < 1 or > 10_000_000)
        {
            throw new FormatException("That recovery key has a missing or mistyped part. Copy it again in full.");
        }

        var body = SealedBox.Open(Stretch(passphrase ?? "", header, iterations), bytes[HeaderBytes..], header);
        var parsed = JsonSerializer.Deserialize<Body>(body) ?? throw new InvalidDataException("The recovery key held no keys.");
        return new RecoveryContents(parsed.ProfileId, parsed.Keys);
    }

    private static byte[] Stretch(string passphrase, byte[] header, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Trim().Normalize(NormalizationForm.FormKC)), header[..SaltBytes], iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private sealed record Body(Guid ProfileId, Dictionary<string, byte[]> Keys);
}
