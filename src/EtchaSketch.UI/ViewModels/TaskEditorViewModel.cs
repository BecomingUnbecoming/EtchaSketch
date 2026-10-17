using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>The form for adding a task or editing one.</summary>
public partial class TaskEditorViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Household _household;
    private readonly Profile _me;
    private readonly TaskItem? _existing;
    private readonly Action<bool> _close;

    public TaskEditorViewModel(AppSession session, Household household, Profile me, TaskItem? existing, Action<bool> close)
    {
        _session = session;
        _household = household;
        _me = me;
        _existing = existing;
        _close = close;

        AssigneeOptions = [new AssigneeOption("Nobody", null), .. household.Members.Select(m => new AssigneeOption(m.DisplayName, m.Id))];

        // Someone else's chore can be edited, but its notes are theirs alone.
        CanEditNotes = existing is null || existing.OwnerId == me.Id;

        Title = existing?.Title ?? "";
        Notes = CanEditNotes ? existing?.Notes : null;
        DueDate = existing?.Due?.ToDateTime(TimeOnly.MinValue);
        Priority = existing?.Priority ?? TaskPriority.Normal;
        IsShared = existing is not null && existing.Tier <= PrivacyTier.Household;
        Repeat = RepeatOptions.Single(option => option.Frequency == existing?.Recurrence?.Frequency);
        RepeatEvery = existing?.Recurrence?.Interval ?? 1;
        Assignee = AssigneeOptions.Single(option => option.ProfileId == existing?.AssigneeId);
        Project = existing?.Project;
        LabelsText = existing is null ? null : string.Join(", ", existing.Labels);

        // Days other than the usual ones (from an import, say) stay available rather than being lost.
        ReminderOptions =
        [
            .. new[] { (int?)null, 1, 2, 3, 7 }.Union([existing?.RemindDaysBefore]).OrderBy(days => days ?? 0).Select(RemindOption.For),
        ];
        Reminder = ReminderOptions.Single(option => option.Days == existing?.RemindDaysBefore);
    }

    public string Heading => _existing is null ? "New task" : "Edit task";

    public bool CanEditNotes { get; }

    public IReadOnlyList<TaskPriority> Priorities { get; } = Enum.GetValues<TaskPriority>();

    public IReadOnlyList<RepeatOption> RepeatOptions { get; } =
    [
        new("Doesn't repeat", null),
        new("Daily", RecurrenceFrequency.Daily),
        new("Weekly", RecurrenceFrequency.Weekly),
        new("Monthly", RecurrenceFrequency.Monthly),
    ];

    public IReadOnlyList<AssigneeOption> AssigneeOptions { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    public partial DateTime? DueDate { get; set; }

    [ObservableProperty]
    public partial TaskPriority Priority { get; set; }

    /// <summary>Shared tasks are chores: everyone in the household sees them on the chore board.</summary>
    [ObservableProperty]
    public partial bool IsShared { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Repeats))]
    public partial RepeatOption Repeat { get; set; }

    public bool Repeats => Repeat.Frequency is not null;

    [ObservableProperty]
    public partial decimal? RepeatEvery { get; set; }

    [ObservableProperty]
    public partial AssigneeOption Assignee { get; set; }

    /// <summary>A group for the task, like "Homelab".</summary>
    [ObservableProperty]
    public partial string? Project { get; set; }

    /// <summary>Labels separated by commas, like "quick-win, b-hands".</summary>
    [ObservableProperty]
    public partial string? LabelsText { get; set; }

    public IReadOnlyList<RemindOption> ReminderOptions { get; }

    [ObservableProperty]
    public partial RemindOption Reminder { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var task = (_existing ?? new TaskItem { HouseholdId = _household.Id, OwnerId = _me.Id, Title = "" }) with
            {
                Title = Title,
                // The copy shown to a non-owner has its notes removed, so keep the stored ones.
                Notes = CanEditNotes ? Notes : _session.Tasks.Get(_existing!.Id)?.Notes,
                Due = DueDate is { } due ? DateOnly.FromDateTime(due) : null,
                Priority = Priority,
                Tier = IsShared ? PrivacyTier.Household : PrivacyTier.Private,
                Recurrence = Repeat.Frequency is { } frequency
                    ? new Recurrence(frequency, (int)Math.Max(1, RepeatEvery ?? 1))
                    : null,
                AssigneeId = Assignee.ProfileId,
                Project = Project,
                Labels = (LabelsText ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                RemindDaysBefore = Reminder.Days,
            };

            if (_existing is null)
            {
                _session.Tasks.Add(task);
            }
            else
            {
                _session.Tasks.Update(task);
            }

            _close(true);
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void Cancel() => _close(false);
}

public sealed record RepeatOption(string Label, RecurrenceFrequency? Frequency)
{
    public override string ToString() => Label;
}

public sealed record AssigneeOption(string Label, Guid? ProfileId)
{
    public override string ToString() => Label;
}

public sealed record RemindOption(string Label, int? Days)
{
    public static RemindOption For(int? days) => days switch
    {
        null => new("No early reminder", null),
        1 => new("A day before", 1),
        7 => new("A week before", 7),
        var n => new($"{n} days before", n),
    };

    public override string ToString() => Label;
}
