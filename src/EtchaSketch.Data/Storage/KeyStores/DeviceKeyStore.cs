namespace EtchaSketch.Data.Storage.KeyStores;

/// <summary>Picks the secure storage for the desktop system the app is running on.</summary>
public static class DeviceKeyStore
{
    /// <param name="appDataDirectory">The app's own data folder. Only Windows keeps files there.</param>
    public static IKeyStore ForDesktop(string appDataDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsKeyStore(Path.Combine(appDataDirectory, "keys"));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeyStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxKeyStore();
        }

        throw new PlatformNotSupportedException("EtchaSketch has no secure key storage for this system yet.");
    }
}
