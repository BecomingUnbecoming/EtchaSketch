using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using EtchaSketch.UI;

namespace EtchaSketch.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            App.Environment = new AppEnvironment(
                System.IO.Path.Combine(FilesDir!.AbsolutePath, "etchasketch.db"),
                new AndroidKeyStore(this),
                DeviceUnlock: new AndroidDeviceUnlock(this),
                ReminderScheduler: new AndroidReminderScheduler(this));

            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }
    }
}
