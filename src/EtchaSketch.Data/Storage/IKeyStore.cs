namespace EtchaSketch.Data.Storage;

/// <summary>
/// The operating system's secure storage for secrets: Windows Credential Manager, macOS Keychain,
/// Linux Secret Service or Android Keystore. Each app head supplies its own implementation.
/// </summary>
public interface IKeyStore
{
    /// <summary>Returns the stored key, or null when there is none.</summary>
    byte[]? Load(string name);

    void Save(string name, byte[] key);
}
