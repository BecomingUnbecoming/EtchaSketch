using System.IO;
using System.Linq;
using Android.Content;
using Android.Security.Keystore;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Storage.KeyStores;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace EtchaSketch.Android;

/// <summary>
/// Keeps keys sealed by a wrapping key that lives inside the Android Keystore. On phones with a
/// security chip, such as Pixels running GrapheneOS, the wrapping key never leaves the chip, so the
/// sealed files are useless when copied off the phone.
/// </summary>
public sealed class AndroidKeyStore(Context context) : IKeyStore
{
    private const string Provider = "AndroidKeyStore";
    private const string WrappingKeyAlias = "etchasketch.wrapping-key";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvBytes = 12;
    private const int TagBits = 128;

    // The no-backup folder keeps sealed keys out of device backups, where they'd be useless anyway.
    private string KeysDirectory => Path.Combine(context.NoBackupFilesDir!.AbsolutePath, "keys");

    public byte[]? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var file = File.ReadAllBytes(path);
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, WrappingKey(), new GCMParameterSpec(TagBits, file, 0, IvBytes));
        return cipher.DoFinal(file, IvBytes, file.Length - IvBytes);
    }

    public void Save(string name, byte[] key)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, WrappingKey());
        var file = cipher.GetIV()!.Concat(cipher.DoFinal(key)!).ToArray();

        Directory.CreateDirectory(KeysDirectory);
        var path = PathFor(name);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, file);
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name) => Path.Combine(KeysDirectory, KeyNames.Validate(name) + ".key");

    private static IKey WrappingKey()
    {
        var keyStore = KeyStore.GetInstance(Provider)!;
        keyStore.Load(null);
        if (keyStore.GetKey(WrappingKeyAlias, null) is { } existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(WrappingKeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }
}
