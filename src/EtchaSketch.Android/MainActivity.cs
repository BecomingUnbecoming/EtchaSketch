using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace EtchaSketch.Android;

[Activity(
    Label = "EtchaSketch",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidDeviceUnlock.CurrentActivity = this;
    }

    protected override void OnDestroy()
    {
        if (AndroidDeviceUnlock.CurrentActivity == this)
        {
            AndroidDeviceUnlock.CurrentActivity = null;
        }

        base.OnDestroy();
    }
}
