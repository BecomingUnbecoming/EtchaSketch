namespace EtchaSketch.Data.Storage;

/// <summary>
/// The database file exists but its key is gone from secure storage, so its contents can't be read.
/// The app should offer recovery from a backup or sync rather than starting over silently.
/// </summary>
public sealed class DatabaseKeyMissingException(string path)
    : Exception($"The database at '{path}' exists, but its key is missing from secure storage.")
{
    public string DatabasePath { get; } = path;
}
