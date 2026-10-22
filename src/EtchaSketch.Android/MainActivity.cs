using System;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using EtchaSketch.UI.Services;
using Avalonia;
using Avalonia.Android;

namespace EtchaSketch.Android;

[Activity(
    Label = "EtchaSketch",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
// An item's QR label ("etchasketch://item/{id}") scanned with the camera opens the item.
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "etchasketch", DataHost = "item")]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>Set on the intent from a reminder's "Done" button: the task to complete.</summary>
    public const string CompleteTaskExtra = "io.github.becomingunbecoming.etchasketch.COMPLETE_TASK";

    /// <summary>The notification the "Done" came from, so it can be cleared.</summary>
    public const string NotificationIdExtra = "io.github.becomingunbecoming.etchasketch.NOTIFICATION_ID";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidDeviceUnlock.CurrentActivity = this;
        HandleReminderAction(this, Intent);
        HandleItemLink(Intent);

        // Android 13 and later ask before an app may show notifications at all.
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleReminderAction(this, intent);
        HandleItemLink(intent);
    }

    // The item opens once someone is signed in, behind their PIN if they have one.
    private static void HandleItemLink(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && ItemLinks.Parse(intent.DataString) is { } itemId)
        {
            intent.SetData(null);
            ItemLinks.RequestOpen(itemId);
        }
    }

    // The task is completed by the home screen once someone is signed in, behind their PIN if they have one.
    private static void HandleReminderAction(Context context, Intent? intent)
    {
        if (intent?.GetStringExtra(CompleteTaskExtra) is { } text && Guid.TryParse(text, out var taskId))
        {
            intent.RemoveExtra(CompleteTaskExtra);
            if (intent.GetIntExtra(NotificationIdExtra, -1) is var id and >= 0)
            {
                (context.GetSystemService(NotificationService) as NotificationManager)?.Cancel(id);
            }

            ReminderActions.RequestComplete(taskId);
        }
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
