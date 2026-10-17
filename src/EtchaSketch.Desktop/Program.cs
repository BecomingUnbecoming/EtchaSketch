using System;
using System.IO;
using EtchaSketch.Data.Storage.KeyStores;
using Avalonia;
using EtchaSketch.UI;

namespace EtchaSketch.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var dataDirectory = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "EtchaSketch");
        App.Environment = new AppEnvironment(
            Path.Combine(dataDirectory, "etchasketch.db"),
            DeviceKeyStore.ForDesktop(dataDirectory));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
