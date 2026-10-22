using EtchaSketch.Data.Storage;

namespace EtchaSketch.Assistant;

/// <summary>
/// Keeps keys as files readable only by the account running the assistant. A server has no
/// keychain; keeping this folder on separate storage from the data does the same job.
/// </summary>
public sealed class FileKeyStore(string directory) : IKeyStore
{
    public byte[]? Load(string name) => File.Exists(PathFor(name)) ? File.ReadAllBytes(PathFor(name)) : null;

    public void Save(string name, byte[] key)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporary = PathFor(name) + ".new";
        File.WriteAllBytes(temporary, []);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllBytes(temporary, key);
        File.Move(temporary, PathFor(name), overwrite: true);
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name) => Path.Combine(directory, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".key");
}
