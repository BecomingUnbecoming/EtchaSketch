namespace EtchaSketch.Data.Storage;

/// <summary>
/// The operating system's secure storage for secrets: Windows Credential Manager, macOS Keychain,
/// Linux Secret Service or Android Keystore. Each app head supplies its own implementation.
/// </summary>
public interface IKeyStore
{
    /// <summary>Returns the stored key, or null when there is none.</summary>
    byte[]? Load(string name);

    /// <summary>Stores the key, replacing any key already stored under that name.</summary>
    void Save(string name, byte[] key);

    /// <summary>Removes the key. Does nothing when there is none.</summary>
    void Delete(string name);
}
