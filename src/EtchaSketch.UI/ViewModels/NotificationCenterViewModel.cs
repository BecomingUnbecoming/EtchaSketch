using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// One person's notification feed and their quiet hours. Opening it marks what it shows as read.
/// </summary>
public partial class NotificationCenterViewModel : ViewModelBase
{
    private static readonly TimeOnly DefaultQuietStart = new(22, 0);
    private static readonly TimeOnly DefaultQuietEnd = new(7, 0);

    private readonly AppSession _session;
    private readonly Profile _me;
    private readonly Action _close;
    private readonly bool _loaded;

    public NotificationCenterViewModel(AppSession session, Profile me, Action close)
    {
        _session = session;
        _me = me;
        _close = close;

        var now = session.Now;
        var feed = session.Notifications.Feed(me.Id, now);
        Items = new ObservableCollection<NotificationRowViewModel>(feed.Shown
            .Select(notification => PrivacyGateway.Project(notification, Audience.ForProfile(me.Id)))
            .OfType<GatewayView>()
            .Select(Row));
        HeldText = feed.HeldUntil is { } until
            ? $"{feed.HeldCount} {(feed.HeldCount == 1 ? "notification" : "notifications")} held until {Clock(until)}"
            : null;

        var hours = session.Notifications.QuietHoursFor(me.Id);
        QuietHoursEnabled = hours is not null;
        QuietStart = (hours?.Start ?? DefaultQuietStart).ToTimeSpan();
        QuietEnd = (hours?.End ?? DefaultQuietEnd).ToTimeSpan();
        _loaded = true;

        session.Notifications.MarkAllRead(me.Id, now);
    }

    public ObservableCollection<NotificationRowViewModel> Items { get; }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>How many notifications are waiting for quiet hours to end, or null when none are.</summary>
    public string? HeldText { get; }

    [ObservableProperty]
    public partial bool QuietHoursEnabled { get; set; }

    [ObservableProperty]
    public partial TimeSpan? QuietStart { get; set; }

    [ObservableProperty]
    public partial TimeSpan? QuietEnd { get; set; }

    /// <summary>Says when a snoozed notification will come back, until the feed is reopened.</summary>
    [ObservableProperty]
    public partial string? SnoozedText { get; private set; }

    [RelayCommand]
    private void Close() => _close();

    private void Snooze(NotificationRowViewModel row, TimeSpan delay)
    {
        var now = _session.Now;
        var until = now + delay;
        _session.Notifications.Snooze(row.Id, until, now);
        Items.Remove(row);
        SnoozedText = $"Snoozed until {Clock(until)}: {row.Title}";
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnQuietHoursEnabledChanged(bool value) => SaveQuietHours();

    partial void OnQuietStartChanged(TimeSpan? value) => SaveQuietHours();

    partial void OnQuietEndChanged(TimeSpan? value) => SaveQuietHours();

    private void SaveQuietHours()
    {
        if (!_loaded)
        {
            return;
        }

        var hours = QuietHoursEnabled
            ? new QuietHours(
                TimeOnly.FromTimeSpan(QuietStart ?? DefaultQuietStart.ToTimeSpan()),
                TimeOnly.FromTimeSpan(QuietEnd ?? DefaultQuietEnd.ToTimeSpan()))
            : null;
        _session.Notifications.SetQuietHours(_me.Id, hours);
    }

    private NotificationRowViewModel Row(GatewayView view)
    {
        var deliveredAt = view.TryGetValue(nameof(Notification.DeliverAt), out var at) && at is DateTimeOffset time ? time : _session.Now;
        return new NotificationRowViewModel(
            view.TryGetValue(nameof(Notification.Id), out var id) && id is Guid guid ? guid : Guid.Empty,
            view.TryGetValue(nameof(Notification.Title), out var title) ? title as string ?? "" : "",
            view.TryGetValue(nameof(Notification.Body), out var body) ? body as string : null,
            WhenText(deliveredAt),
            view.TryGetValue(nameof(Notification.ReadAt), out var read) && read is null,
            Snooze);
    }

    private string WhenText(DateTimeOffset time)
    {
        var local = _session.Local(time);
        return DateOnly.FromDateTime(local.DateTime) == _session.Today
            ? Clock(time)
            : local.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);
    }

    private string Clock(DateTimeOffset time) => _session.Local(time).ToString("HH:mm", CultureInfo.CurrentCulture);
}
