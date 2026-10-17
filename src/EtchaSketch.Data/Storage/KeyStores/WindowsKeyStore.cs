using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace EtchaSketch.Data.Storage.KeyStores;

/// <summary>
/// Keeps keys in files sealed with Windows DPAPI, which ties them to your Windows account. Another
/// account, or the same files copied to another computer, can't unseal them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKeyStore(string directory) : IKeyStore
{
    public byte[]? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        return ProtectedData.Unprotect(File.ReadAllBytes(path), EntropyFor(name), DataProtectionScope.CurrentUser);
    }

    public void Save(string name, byte[] key)
    {
        Directory.CreateDirectory(directory);
        var sealedKey = ProtectedData.Protect(key, EntropyFor(name), DataProtectionScope.CurrentUser);

        // Write beside the target, then swap it in, so a crash mid-write can't leave half a key.
        var path = PathFor(name);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, sealedKey);
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name) => Path.Combine(directory, KeyNames.Validate(name) + ".key");

    // Binding each sealed file to its name stops one key's file being swapped in for another.
    private static byte[] EntropyFor(string name) => Encoding.UTF8.GetBytes("EtchaSketch:" + name);
}
