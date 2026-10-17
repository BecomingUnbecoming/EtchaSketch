using System.Security.Cryptography;
using EtchaSketch.Data.Sync;

namespace EtchaSketch.Tests.Sync;

public sealed class RecoveryKeyTests
{
    private const string Passphrase = "correct horse battery";
    private const int Fast = 1_000;

    private static readonly Guid Person = Guid.NewGuid();

    private static RecoveryContents Contents() => new(Person, new Dictionary<string, byte[]>
    {
        ["household:abc"] = Enumerable.Repeat((byte)7, 32).ToArray(),
        ["person:def"] = Enumerable.Repeat((byte)9, 32).ToArray(),
    });

    [Fact]
    public void The_right_passphrase_brings_every_key_back()
    {
        var text = RecoveryKey.Create(Passphrase, Contents(), Fast);

        var opened = RecoveryKey.Open(Passphrase, text);

        Assert.Equal(Person, opened.ProfileId);
        Assert.Equal(Contents().Keys.OrderBy(k => k.Key).Select(k => (k.Key, Convert.ToHexString(k.Value))),
            opened.Keys.OrderBy(k => k.Key).Select(k => (k.Key, Convert.ToHexString(k.Value))));
    }

    [Fact]
    public void The_text_holds_no_readable_key_or_name()
    {
        var text = RecoveryKey.Create(Passphrase, Contents(), Fast);

        Assert.StartsWith("ESKEY1-", text);
        Assert.DoesNotContain("household", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Person.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wrong_passphrase_opens_nothing()
    {
        var text = RecoveryKey.Create(Passphrase, Contents(), Fast);

        Assert.ThrowsAny<CryptographicException>(() => RecoveryKey.Open("wrong passphrase here", text));
    }

    [Fact]
    public void Spaces_and_line_breaks_from_copying_do_not_matter()
    {
        var text = RecoveryKey.Create(Passphrase, Contents(), Fast);
        var wrapped = string.Join("\n  ", text.Chunk(40).Select(chunk => new string(chunk)));

        Assert.Equal(Person, RecoveryKey.Open($" {Passphrase} ", wrapped).ProfileId);
    }

    [Fact]
    public void A_changed_character_is_refused()
    {
        var text = RecoveryKey.Create(Passphrase, Contents(), Fast);
        var middle = text.Length / 2;
        var changed = text[..middle] + (text[middle] == 'A' ? 'B' : 'A') + text[(middle + 1)..];

        Assert.ThrowsAny<Exception>(() => RecoveryKey.Open(Passphrase, changed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("ESKEY1-")]
    [InlineData("ESKEY1-AAAA")]
    [InlineData("ESKEY1-!!!!")]
    public void Text_that_is_not_a_whole_recovery_key_is_explained(string text)
    {
        Assert.Throws<FormatException>(() => RecoveryKey.Open(Passphrase, text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("           ")]
    public void A_short_passphrase_is_refused(string passphrase)
    {
        Assert.Throws<ArgumentException>(() => RecoveryKey.Create(passphrase, Contents(), Fast));
    }

    [Fact]
    public void Two_keys_made_from_the_same_keys_differ()
    {
        Assert.NotEqual(RecoveryKey.Create(Passphrase, Contents(), Fast), RecoveryKey.Create(Passphrase, Contents(), Fast));
    }
}
