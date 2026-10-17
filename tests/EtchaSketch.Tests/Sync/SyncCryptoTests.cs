using System.Security.Cryptography;
using System.Text;
using EtchaSketch.Data.Sync;

namespace EtchaSketch.Tests.Sync;

public class SyncCryptoTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void A_sealed_change_opens_with_the_same_key_and_stream()
    {
        var stream = StreamKey.From(Key);
        var sealedChange = stream.Seal(Encoding.UTF8.GetBytes("Dishes"));

        Assert.Equal("Dishes", Encoding.UTF8.GetString(stream.Open(sealedChange)));
        Assert.DoesNotContain("Dishes", Encoding.UTF8.GetString(sealedChange));
    }

    [Fact]
    public void Sealing_the_same_text_twice_looks_different()
    {
        var stream = StreamKey.From(Key);

        Assert.NotEqual(stream.Seal([1, 2, 3]), stream.Seal([1, 2, 3]));
    }

    [Fact]
    public void Another_key_cannot_open_a_change()
    {
        var sealedChange = StreamKey.From(Key).Seal([1, 2, 3]);

        Assert.ThrowsAny<CryptographicException>(() => StreamKey.From(RandomNumberGenerator.GetBytes(32)).Open(sealedChange));
    }

    [Fact]
    public void A_tampered_change_is_rejected()
    {
        var stream = StreamKey.From(Key);
        var sealedChange = stream.Seal([1, 2, 3]);
        sealedChange[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => stream.Open(sealedChange));
    }

    [Fact]
    public void Stream_ids_and_tokens_come_from_the_key_and_give_nothing_away()
    {
        var stream = StreamKey.From(Key);
        var other = StreamKey.From(RandomNumberGenerator.GetBytes(32));

        Assert.Equal(stream.StreamId, StreamKey.From(Key).StreamId);
        Assert.NotEqual(stream.StreamId, other.StreamId);
        Assert.NotEqual(stream.StreamId, stream.AccessToken);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", stream.StreamId);
    }

    [Fact]
    public void Pairing_codes_are_long_random_and_easy_to_type()
    {
        var code = PairingCode.New();

        Assert.Matches("^([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$", code.Text);
        Assert.NotEqual(code.Text, PairingCode.New().Text);
    }

    [Fact]
    public void A_pairing_code_reads_back_however_it_is_typed()
    {
        var code = PairingCode.New();

        var typed = PairingCode.Parse(" " + code.Text.ToLowerInvariant().Replace("-", " ") + " ");

        Assert.Equal(code.PairingId, typed.PairingId);
        Assert.Equal("hi", Encoding.UTF8.GetString(typed.Open(code.Seal(Encoding.UTF8.GetBytes("hi")))));
    }

    [Fact]
    public void A_mistyped_pairing_code_is_refused()
    {
        Assert.Throws<FormatException>(() => PairingCode.Parse("ABCD-EFGH"));
        Assert.Throws<FormatException>(() => PairingCode.Parse(new string('U', 32)));
    }
}
