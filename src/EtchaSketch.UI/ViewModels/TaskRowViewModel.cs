using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Tasks;

namespace EtchaSketch.UI.ViewModels;

/// <summary>One task as a row on screen. Its text comes only from the gateway's view of the task.</summary>
public sealed class TaskRowViewModel
{
    public TaskRowViewModel(
        TaskItem task,
        GatewayView view,
        DateOnly today,
        string? assigneeName,
        bool canDelete,
        Action complete,
        Action edit,
        Action delete)
    {
        Title = view.TryGetValue(nameof(TaskItem.Title), out var title) ? title as string ?? "" : "";
        Notes = view.TryGetValue(nameof(TaskItem.Notes), out var notes) ? notes as string : null;
        CanDelete = canDelete;
        CompleteCommand = new RelayCommand(complete);
        EditCommand = new RelayCommand(edit);
        DeleteCommand = new RelayCommand(delete, () => canDelete);

        if (task.Due is { } due)
        {
            IsOverdue = due < today;
            DueText = due == today ? "Today"
                : due == today.AddDays(1) ? "Tomorrow"
                : IsOverdue ? $"Overdue · {ShortDate(due)}"
                : ShortDate(due);
        }

        var details = new List<string>();
        Project = task.Project;
        Labels = task.Labels;
        if (task.Project is { } project)
        {
            details.Add(project);
        }

        if (task.Labels.Count > 0)
        {
            details.Add(string.Join(" ", task.Labels.Select(label => "#" + label)));
        }

        if (task.Priority != TaskPriority.Normal)
        {
            details.Add($"{task.Priority} priority");
        }

        if (task.Recurrence is { } recurrence)
        {
            details.Add(RepeatText(recurrence));
        }

        if (assigneeName is not null)
        {
            details.Add(assigneeName);
        }

        if (task.Tier <= PrivacyTier.Household)
        {
            details.Add("Shared");
        }

        Details = string.Join(" · ", details);
    }

    public string Title { get; }

    public string? Project { get; }

    public IReadOnlyList<string> Labels { get; }

    /// <summary>Your notes on your own task. Always null on someone else's.</summary>
    public string? Notes { get; }

    public bool HasNotes => !string.IsNullOrEmpty(Notes);

    public string? DueText { get; }

    public bool IsOverdue { get; }

    public string Details { get; }

    public bool CanDelete { get; }

    public IRelayCommand CompleteCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public static string RepeatText(Recurrence recurrence) => (recurrence.Frequency, recurrence.Interval) switch
    {
        (RecurrenceFrequency.Daily, 1) => "Every day",
        (RecurrenceFrequency.Weekly, 1) => "Every week",
        (RecurrenceFrequency.Monthly, 1) => "Every month",
        (RecurrenceFrequency.Daily, var n) => $"Every {n} days",
        (RecurrenceFrequency.Weekly, var n) => $"Every {n} weeks",
        (_, var n) => $"Every {n} months",
    };

    private static string ShortDate(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.CurrentCulture);
}
