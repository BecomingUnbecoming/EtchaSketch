using System.Runtime.Versioning;
using System.Security.Cryptography;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Storage.KeyStores;

namespace EtchaSketch.Tests.Storage;

/// <summary>
/// Runs the same checks against each system's real secure storage. Each test only runs on its own
/// system; CI runs them on Windows, macOS and Linux.
/// </summary>
public sealed class KeyStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-keys-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_store_keeps_keys() => VerifyContract(new WindowsKeyStore(_directory));

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_store_never_writes_the_key_in_the_clear()
    {
        var store = new WindowsKeyStore(_directory);
        var key = RandomNumberGenerator.GetBytes(32);
        store.Save("etchasketch.test", key);

        var file = File.ReadAllBytes(Path.Combine(_directory, "etchasketch.test.key"));
        Assert.DoesNotContain(Convert.ToHexString(key), Convert.ToHexString(file));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void Mac_store_keeps_keys() => VerifyContract(new MacKeyStore());

    // CI's keyring has no default collection, so the test uses the in-memory session collection.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Linux_store_keeps_keys() =>
        VerifyContract(new LinuxKeyStore(collection: "/org/freedesktop/secrets/aliases/session"));

    [Fact]
    public void Key_names_are_restricted()
    {
        Assert.Throws<ArgumentException>(() => KeyNames.Validate("../escape"));
        Assert.Throws<ArgumentException>(() => KeyNames.Validate(""));
        Assert.Throws<ArgumentException>(() => KeyNames.Validate("Has Spaces"));
        Assert.Equal(EncryptedDatabase.KeyName, KeyNames.Validate(EncryptedDatabase.KeyName));
    }

    private static void VerifyContract(IKeyStore store)
    {
        // A unique name per run keeps tests from touching real keys or each other.
        var name = $"etchasketch.test-{Guid.NewGuid():n}";
        var other = name + "-other";
        try
        {
            Assert.Null(store.Load(name));

            var first = RandomNumberGenerator.GetBytes(32);
            store.Save(name, first);
            Assert.Equal(first, store.Load(name));

            var second = RandomNumberGenerator.GetBytes(32);
            store.Save(name, second);
            Assert.Equal(second, store.Load(name));

            store.Save(other, first);
            Assert.Equal(second, store.Load(name));
            Assert.Equal(first, store.Load(other));

            store.Delete(name);
            Assert.Null(store.Load(name));
            Assert.Equal(first, store.Load(other));

            store.Delete(name);
        }
        finally
        {
            store.Delete(name);
            store.Delete(other);
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS only";
        }
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
        else if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is null)
        {
            Skip = "Needs a desktop session with a Secret Service";
        }
    }
}
