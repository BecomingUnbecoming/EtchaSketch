using System.Security.Cryptography;
using System.Text;

namespace EtchaSketch.Data.Sync;

/// <summary>
/// A one-time code that carries keys from a device you have to a new one. It holds 160 random
/// bits, written as 32 characters in groups of four. The server files the sealed keys under an id
/// derived from the code, so it never sees the code itself and can't open what it holds.
/// </summary>
public sealed class PairingCode
{
    // Crockford's base32: no I, L, O or U, so the code is hard to misread.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int SecretBytes = 20;

    private readonly byte[] _sealKey;
    private readonly byte[] _associatedData;

    private PairingCode(byte[] secret)
    {
        Text = Format(secret);
        PairingId = Convert.ToHexStringLower(SealedBox.Derive(secret, "etchasketch pairing id", 16));
        _sealKey = SealedBox.Derive(secret, "etchasketch pairing seal");
        _associatedData = Encoding.UTF8.GetBytes(PairingId);
    }

    /// <summary>The code as people read and type it, like 7K2M-…-Q4XD.</summary>
    public string Text { get; }

    /// <summary>What the server files the sealed keys under.</summary>
    public string PairingId { get; }

    public static PairingCode New() => new(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Reads a typed code, forgiving case, spaces, dashes and the letters O, I and L.</summary>
    /// <exception cref="FormatException">It isn't a whole pairing code.</exception>
    public static PairingCode Parse(string text)
    {
        var symbols = new StringBuilder();
        foreach (var c in text.ToUpperInvariant())
        {
            switch (c)
            {
                case ' ' or '-':
                    continue;
                case 'O':
                    symbols.Append('0');
                    break;
                case 'I' or 'L':
                    symbols.Append('1');
                    break;
                default:
                    symbols.Append(c);
                    break;
            }
        }

        if (symbols.Length != SecretBytes * 8 / 5 || symbols.ToString().Any(c => !Alphabet.Contains(c)))
        {
            throw new FormatException("That isn't a whole pairing code.");
        }

        var secret = new byte[SecretBytes];
        for (var i = 0; i < symbols.Length; i++)
        {
            var value = Alphabet.IndexOf(symbols[i]);
            for (var bit = 0; bit < 5; bit++)
            {
                if ((value & (1 << (4 - bit))) != 0)
                {
                    var position = i * 5 + bit;
                    secret[position / 8] |= (byte)(0x80 >> (position % 8));
                }
            }
        }

        return new PairingCode(secret);
    }

    public byte[] Seal(byte[] plaintext) => SealedBox.Seal(_sealKey, plaintext, _associatedData);

    public byte[] Open(byte[] message) => SealedBox.Open(_sealKey, message, _associatedData);

    private static string Format(byte[] secret)
    {
        var text = new StringBuilder();
        for (var i = 0; i < secret.Length * 8 / 5; i++)
        {
            var value = 0;
            for (var bit = 0; bit < 5; bit++)
            {
                var position = i * 5 + bit;
                value = (value << 1) | ((secret[position / 8] >> (7 - position % 8)) & 1);
            }

            if (i > 0 && i % 4 == 0)
            {
                text.Append('-');
            }

            text.Append(Alphabet[value]);
        }

        return text.ToString();
    }
}
