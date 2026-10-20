using System;
using CommunityToolkit.Mvvm.Input;

namespace EtchaSketch.UI.ViewModels;

/// <summary>One notification as the feed shows it, with a way to bring it back later.</summary>
public sealed partial class NotificationRowViewModel(
    Guid id, string title, string? body, string whenText, bool isUnread, Action<NotificationRowViewModel, TimeSpan> snooze)
    : ViewModelBase
{
    public Guid Id { get; } = id;

    public string Title { get; } = title;

    public string? Body { get; } = body;

    public string WhenText { get; } = whenText;

    public bool IsUnread { get; } = isUnread;

    public bool HasBody => !string.IsNullOrEmpty(Body);

    [RelayCommand]
    private void SnoozeTenMinutes() => snooze(this, TimeSpan.FromMinutes(10));

    [RelayCommand]
    private void SnoozeAnHour() => snooze(this, TimeSpan.FromHours(1));
}
