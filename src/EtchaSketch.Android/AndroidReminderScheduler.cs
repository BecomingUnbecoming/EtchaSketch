using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Android.App;
using Android.Content;
using Android.OS;
using EtchaSketch.Data.Notifications;
using EtchaSketch.UI.Services;

namespace EtchaSketch.Android;

/// <summary>
/// Gives reminders to Android's own alarm clock, so they show at their time with the app closed,
/// after a restart, and without any server or Google service. What is scheduled is also saved in
/// the app's private storage, so it can be re-armed after the phone restarts or its clock changes.
/// </summary>
public sealed class AndroidReminderScheduler(Context context) : IReminderScheduler
{
    private const string Preferences = "reminders";
    private const string SavedKey = "scheduled";

    public void Replace(IReadOnlyList<PlannedReminder> reminders)
    {
        var wanted = reminders.Select(ScheduledReminder.From).ToDictionary(reminder => reminder.Key);
        var saved = Load().ToDictionary(reminder => reminder.Key);
        if (wanted.Count == saved.Count && wanted.All(pair => saved.TryGetValue(pair.Key, out var old) && old == pair.Value))
        {
            return;
        }

        foreach (var old in saved.Values.Where(old => !wanted.TryGetValue(old.Key, out var now) || now != old))
        {
            Cancel(context, old);
        }

        foreach (var reminder in wanted.Values.Where(reminder => !saved.TryGetValue(reminder.Key, out var old) || old != reminder))
        {
            Arm(context, reminder);
        }

        Save([.. wanted.Values]);
    }

    /// <summary>Schedules everything saved again. The alarm clock forgets on restart, and a new time zone moves wall-clock times.</summary>
    public void Rearm()
    {
        foreach (var reminder in Load())
        {
            Cancel(context, reminder);
            Arm(context, reminder);
        }
    }

    /// <summary>Shows a reminder again after a delay, for "Snooze" on the notification.</summary>
    public static void Snooze(Context context, ScheduledReminder reminder, TimeSpan delay) =>
        Arm(context, reminder with { Key = reminder.Key + ":snoozed", At = DateTime.Now + delay });

    internal static void Arm(Context context, ScheduledReminder reminder)
    {
        var at = new DateTimeOffset(reminder.At, TimeZoneInfo.Local.GetUtcOffset(reminder.At));
        if (at <= DateTimeOffset.Now || context.GetSystemService(Context.AlarmService) is not AlarmManager alarms)
        {
            return;
        }

        var show = ShowIntent(context, reminder);
        var millis = at.ToUnixTimeMilliseconds();
        var exactAllowed = !OperatingSystem.IsAndroidVersionAtLeast(31) || alarms.CanScheduleExactAlarms();
        if (reminder.Alarm && exactAllowed)
        {
            // Shown in the status bar and the system clock like any alarm, and never delayed to save battery.
            var open = PendingIntent.GetActivity(context, RequestCode(reminder.Key) ^ 1,
                new Intent(context, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            alarms.SetAlarmClock(new AlarmManager.AlarmClockInfo(millis, open), show);
        }
        else if (exactAllowed && OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, millis, show);
        }
        else
        {
            // Without exact-alarm permission Android may move it a few minutes; it still comes.
            alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, millis, show);
        }
    }

    private static void Cancel(Context context, ScheduledReminder reminder)
    {
        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms)
        {
            alarms.Cancel(ShowIntent(context, reminder));
        }
    }

    private static PendingIntent ShowIntent(Context context, ScheduledReminder reminder)
    {
        var intent = new Intent(context, typeof(ReminderReceiver)).SetAction(ReminderReceiver.ActionShow);
        reminder.WriteTo(intent);
        return PendingIntent.GetBroadcast(context, RequestCode(reminder.Key), intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>A stable number for a reminder, so the same reminder replaces itself rather than doubling up.</summary>
    internal static int RequestCode(string key) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) & 0x7FFFFFFF;

    private List<ScheduledReminder> Load()
    {
        var text = context.GetSharedPreferences(Preferences, FileCreationMode.Private)?.GetString(SavedKey, null);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(text, ReminderJson.Default.ListScheduledReminder) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save(List<ScheduledReminder> reminders)
    {
        using var editor = context.GetSharedPreferences(Preferences, FileCreationMode.Private)!.Edit()!;
        editor.PutString(SavedKey, JsonSerializer.Serialize(reminders, ReminderJson.Default.ListScheduledReminder));
        editor.Apply();
    }
}

/// <summary>One reminder as it is handed to the alarm clock and saved for re-arming.</summary>
/// <param name="At">Local wall-clock time, so a time-zone change keeps it at the same time of day.</param>
public sealed record ScheduledReminder(string Key, string TaskId, string Title, string? Body, DateTime At, bool Alarm)
{
    private const string Extra = "io.github.becomingunbecoming.etchasketch.reminder.";

    public static ScheduledReminder From(PlannedReminder planned) =>
        new(planned.Key, planned.TaskId.ToString(), planned.Title, planned.Body, planned.At, planned.Alarm);

    public void WriteTo(Intent intent)
    {
        intent.PutExtra(Extra + "key", Key);
        intent.PutExtra(Extra + "task", TaskId);
        intent.PutExtra(Extra + "title", Title);
        intent.PutExtra(Extra + "body", Body);
        intent.PutExtra(Extra + "at", At.Ticks);
        intent.PutExtra(Extra + "alarm", Alarm);
    }

    public static ScheduledReminder? ReadFrom(Intent? intent) =>
        intent?.GetStringExtra(Extra + "key") is { } key
            ? new ScheduledReminder(key, intent.GetStringExtra(Extra + "task") ?? "", intent.GetStringExtra(Extra + "title") ?? "",
                intent.GetStringExtra(Extra + "body"), new DateTime(intent.GetLongExtra(Extra + "at", 0)), intent.GetBooleanExtra(Extra + "alarm", false))
            : null;
}

[JsonSerializable(typeof(List<ScheduledReminder>))]
internal sealed partial class ReminderJson : JsonSerializerContext;
