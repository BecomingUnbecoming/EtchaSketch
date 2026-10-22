using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    // Links and comments save as soon as they're made, so closing without saving still changed something.
    private bool _changedAlready;

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

        DueTime = existing?.DueTime?.ToTimeSpan();
        MinutesOptions =
        [
            .. new[] { (int?)null, 5, 10, 15, 30, 60, 120 }.Union([existing?.RemindMinutesBefore])
                .OrderBy(minutes => minutes ?? 0).Select(MinutesOption.For),
        ];
        RemindBefore = MinutesOptions.Single(option => option.Minutes == existing?.RemindMinutesBefore);
        IsAlarm = existing?.Alarm ?? false;

        // Only a top-level task can have steps, and a task with steps can't be one.
        var candidates = session.Tasks.VisibleTo(me.Id).Where(task => task.Id != existing?.Id).ToList();
        CanBeStep = existing is null || !session.Tasks.HasSubtasks(existing.Id);
        ParentOptions =
        [
            new ParentOption("Not a step of another task", null),
            .. candidates.Where(task => task.ParentId is null).Select(task => new ParentOption(task.Title, task.Id)),
        ];
        Parent = ParentOptions.FirstOrDefault(option => option.TaskId == existing?.ParentId) ?? ParentOptions[0];

        LinkTargets = [.. candidates.Select(task => new ParentOption(task.Title, task.Id))];
        NewLinkKind = LinkKinds[0];
        LoadLinks();
        LoadComments();
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
    [NotifyPropertyChangedFor(nameof(CanHaveTime))]
    public partial DateTime? DueDate { get; set; }

    /// <summary>A time needs a date to belong to.</summary>
    public bool CanHaveTime => DueDate is not null;

    /// <summary>An optional time of day on the due date. With one, reminders come at that time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTime))]
    public partial TimeSpan? DueTime { get; set; }

    public bool HasTime => DueTime is not null;

    public IReadOnlyList<MinutesOption> MinutesOptions { get; }

    [ObservableProperty]
    public partial MinutesOption RemindBefore { get; set; }

    /// <summary>Remind like an alarm: louder on a phone, and it comes through quiet hours.</summary>
    [ObservableProperty]
    public partial bool IsAlarm { get; set; }

    [RelayCommand]
    private void ClearTime() => DueTime = null;

    /// <summary>The task this one is a step of. A task with steps of its own can't become a step.</summary>
    public IReadOnlyList<ParentOption> ParentOptions { get; }

    [ObservableProperty]
    public partial ParentOption Parent { get; set; }

    public bool CanBeStep { get; }

    /// <summary>Links and comments belong to a saved task, so they show once it exists.</summary>
    public bool IsExisting => _existing is not null;

    public ObservableCollection<LinkRowViewModel> Links { get; } = [];

    public IReadOnlyList<LinkKindOption> LinkKinds { get; } =
    [
        new("Blocks", TaskLinkKind.Blocks, ThisFirst: true),
        new("Is blocked by", TaskLinkKind.Blocks, ThisFirst: false),
        new("Is related to", TaskLinkKind.Related, ThisFirst: true),
    ];

    [ObservableProperty]
    public partial LinkKindOption NewLinkKind { get; set; }

    public IReadOnlyList<ParentOption> LinkTargets { get; }

    [ObservableProperty]
    public partial ParentOption? NewLinkTarget { get; set; }

    public ObservableCollection<CommentRowViewModel> Comments { get; } = [];

    [ObservableProperty]
    public partial string? NewComment { get; set; }

    [RelayCommand]
    private void AddLink()
    {
        if (_existing is null || NewLinkTarget?.TaskId is not { } other)
        {
            return;
        }

        try
        {
            var link = NewLinkKind.ThisFirst
                ? new TaskLink(_existing.Id, other, NewLinkKind.Kind)
                : new TaskLink(other, _existing.Id, NewLinkKind.Kind);
            _session.Tasks.AddLink(link, _session.Now);
            _changedAlready = true;
            ErrorMessage = null;
            NewLinkTarget = null;
            LoadLinks();
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void AddComment()
    {
        if (_existing is null)
        {
            return;
        }

        try
        {
            _session.Tasks.AddComment(_existing.Id, _me.Id, NewComment ?? "", _session.Now);
            _changedAlready = true;
            ErrorMessage = null;
            NewComment = null;
            LoadComments();
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = Messages.Friendly(exception);
        }
    }

    private void LoadLinks()
    {
        Links.Clear();
        if (_existing is null)
        {
            return;
        }

        foreach (var link in _session.Tasks.LinksOf(_existing.Id, _me.Id))
        {
            var stored = link.OtherIsFirst
                ? new TaskLink(link.Other.Id, _existing.Id, link.Kind)
                : new TaskLink(_existing.Id, link.Other.Id, link.Kind);
            Links.Add(new LinkRowViewModel(link.Label, link.Other.Title, () =>
            {
                _session.Tasks.RemoveLink(stored);
                _changedAlready = true;
                LoadLinks();
            }));
        }
    }

    private void LoadComments()
    {
        Comments.Clear();
        if (_existing is null)
        {
            return;
        }

        foreach (var comment in _session.Tasks.CommentsOf(_existing.Id, _me.Id))
        {
            var author = _household.Members.FirstOrDefault(member => member.Id == comment.AuthorId)?.DisplayName ?? "Someone";
            var when = _session.Local(comment.CreatedAt).ToString("ddd d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
            Comments.Add(new CommentRowViewModel($"{author} · {when}", comment.Text));
        }
    }



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
                // Without a date there's no time, and without a time no minutes-before reminder or alarm.
                DueTime = DueDate is not null && DueTime is { } time ? TimeOnly.FromTimeSpan(time) : null,
                RemindMinutesBefore = DueDate is not null && DueTime is not null ? RemindBefore.Minutes : null,
                Alarm = DueDate is not null && DueTime is not null && IsAlarm,
                ParentId = CanBeStep ? Parent.TaskId : _existing?.ParentId,
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
    private void Cancel() => _close(_changedAlready);
}

public sealed record RepeatOption(string Label, RecurrenceFrequency? Frequency)
{
    public override string ToString() => Label;
}

public sealed record AssigneeOption(string Label, Guid? ProfileId)
{
    public override string ToString() => Label;
}

public sealed record ParentOption(string Label, Guid? TaskId)
{
    public override string ToString() => Label;
}

public sealed record LinkKindOption(string Label, TaskLinkKind Kind, bool ThisFirst)
{
    public override string ToString() => Label;
}

/// <summary>One link in the editor, like "Blocked by · Install the SDK", with a way to remove it.</summary>
public sealed class LinkRowViewModel(string label, string title, Action remove)
{
    public string Text { get; } = $"{label} · {title}";

    public IRelayCommand RemoveCommand { get; } = new RelayCommand(remove);
}

public sealed record CommentRowViewModel(string Byline, string Text);

public sealed record MinutesOption(string Label, int? Minutes)
{
    public static MinutesOption For(int? minutes) => minutes switch
    {
        null => new("Only at the time", null),
        60 => new("Also an hour before", 60),
        var n when n % 60 == 0 => new($"Also {n / 60} hours before", n),
        var n => new($"Also {n} minutes before", n),
    };

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
