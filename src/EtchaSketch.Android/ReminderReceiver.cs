using System;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;

namespace EtchaSketch.Android;

/// <summary>
/// Shows a reminder when the alarm clock says it's time, and handles "Snooze" pressed on one. On the
/// lock screen it says only "EtchaSketch reminder"; the task shows once the phone is unlocked.
/// </summary>
[BroadcastReceiver(Exported = false)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public const string ActionShow = "io.github.becomingunbecoming.etchasketch.SHOW_REMINDER";
    public const string ActionSnooze = "io.github.becomingunbecoming.etchasketch.SNOOZE_REMINDER";

    private const string RemindersChannel = "reminders";
    private const string AlarmsChannel = "alarms";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || ScheduledReminder.ReadFrom(intent) is not { } reminder)
        {
            return;
        }

        var notifications = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        var id = AndroidReminderScheduler.RequestCode(reminder.Key.Replace(":snoozed", "", StringComparison.Ordinal));
        if (intent!.Action == ActionSnooze)
        {
            notifications.Cancel(id);
            AndroidReminderScheduler.Snooze(context, reminder, TimeSpan.FromMinutes(10));
            return;
        }

        CreateChannels(context, notifications);
        notifications.Notify(id, Build(context, reminder, id));
    }

    private static Notification Build(Context context, ScheduledReminder reminder, int id)
    {
        var channel = reminder.Alarm ? AlarmsChannel : RemindersChannel;
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(context, channel)
            : new Notification.Builder(context);

        var open = PendingIntent.GetActivity(context, id, new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var done = new Intent(context, typeof(MainActivity))
            .PutExtra(MainActivity.CompleteTaskExtra, reminder.TaskId)
            .PutExtra(MainActivity.NotificationIdExtra, id);
        var doneIntent = PendingIntent.GetActivity(context, id ^ 2, done, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var snooze = new Intent(context, typeof(ReminderReceiver)).SetAction(ActionSnooze);
        reminder.WriteTo(snooze);
        var snoozeIntent = PendingIntent.GetBroadcast(context, id ^ 3, snooze, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        // What the lock screen shows instead, so a task's title isn't readable on a locked phone.
        var hidden = (OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(context, channel) : new Notification.Builder(context))
            .SetSmallIcon(context.ApplicationInfo!.Icon)
            .SetContentTitle("EtchaSketch reminder")
            .Build();

        builder
            .SetSmallIcon(context.ApplicationInfo!.Icon)
            .SetContentTitle(reminder.Title)
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .SetVisibility(NotificationVisibility.Private)
            .SetPublicVersion(hidden)
            .SetCategory(reminder.Alarm ? Notification.CategoryAlarm : Notification.CategoryReminder)
            .AddAction(new Notification.Action.Builder(0, "Done", doneIntent).Build())
            .AddAction(new Notification.Action.Builder(0, "Snooze 10 min", snoozeIntent).Build());

        if (!string.IsNullOrEmpty(reminder.Body))
        {
            builder.SetContentText(reminder.Body);
        }

        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            builder.SetPriority(reminder.Alarm ? (int)NotificationPriority.Max : (int)NotificationPriority.High);
            builder.SetDefaults(NotificationDefaults.All);
        }

        var notification = builder.Build()!;
        if (reminder.Alarm)
        {
            // Keeps sounding until it's dismissed or acted on, like an alarm clock.
            notification.Flags |= NotificationFlags.Insistent;
        }

        return notification;
    }

    private static void CreateChannels(Context context, NotificationManager notifications)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        var reminders = new NotificationChannel(RemindersChannel, "Reminders", NotificationImportance.High)
        {
            Description = "Tasks coming due",
            LockscreenVisibility = NotificationVisibility.Private,
        };

        var alarms = new NotificationChannel(AlarmsChannel, "Alarms", NotificationImportance.High)
        {
            Description = "Tasks set as alarms. Louder, and they keep going until dismissed.",
            LockscreenVisibility = NotificationVisibility.Private,
        };
        alarms.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Alarm),
            new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Alarm)!.SetContentType(AudioContentType.Sonification)!.Build());
        alarms.EnableVibration(true);

        // Creating a channel that exists already changes nothing, so this is safe every time.
        notifications.CreateNotificationChannel(reminders);
        notifications.CreateNotificationChannel(alarms);
    }
}

/// <summary>Re-arms saved reminders after a restart, an app update, or a change of clock or time zone.</summary>
[BroadcastReceiver(Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, Intent.ActionTimeChanged, Intent.ActionTimezoneChanged })]
public sealed class RearmRemindersReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null)
        {
            new AndroidReminderScheduler(context).Rearm();
        }
    }
}
