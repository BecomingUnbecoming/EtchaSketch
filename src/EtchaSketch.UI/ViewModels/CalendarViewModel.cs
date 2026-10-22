using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Calendar;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

public enum CalendarMode
{
    Day,
    Week,
    Month,
}

/// <summary>
/// One calendar: tasks, chores, events, time blocks, upkeep, warranties and meals, by day, week
/// or month. Events are added and edited here; everything else is edited where it lives.
/// </summary>
public partial class CalendarViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Func<Household> _household;
    private readonly Profile _me;
    private readonly Action _changed;
    private Guid? _editing;

    public CalendarViewModel(AppSession session, Func<Household> household, Profile me, Action changed)
    {
        _session = session;
        _household = household;
        _me = me;
        _changed = changed;
        Anchor = session.Today;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonth), nameof(IsList))]
    public partial CalendarMode Mode { get; set; } = CalendarMode.Week;

    partial void OnModeChanged(CalendarMode value) => Refresh();

    public bool IsMonth => Mode == CalendarMode.Month;

    public bool IsList => !IsMonth;

    /// <summary>A day inside what's shown.</summary>
    [ObservableProperty]
    public partial DateOnly Anchor { get; private set; }

    /// <summary>"Week of 19 Oct 2026", "October 2026" or "Wednesday 21 Oct 2026".</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = "";

    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];

    /// <summary>The weekday names over the month grid, starting on the first day of the week.</summary>
    public IReadOnlyList<string> WeekdayNames
    {
        get
        {
            var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            return Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(first + i) % 7]).ToList();
        }
    }

    [RelayCommand]
    private void ShowDay() => Mode = CalendarMode.Day;

    [RelayCommand]
    private void ShowWeek() => Mode = CalendarMode.Week;

    [RelayCommand]
    private void ShowMonth() => Mode = CalendarMode.Month;

    [RelayCommand]
    private void Previous() => Move(-1);

    [RelayCommand]
    private void Next() => Move(1);

    [RelayCommand]
    private void Today()
    {
        Anchor = _session.Today;
        Refresh();
    }

    private void Move(int step)
    {
        Anchor = Mode switch
        {
            CalendarMode.Day => Anchor.AddDays(step),
            CalendarMode.Week => Anchor.AddDays(7 * step),
            _ => Anchor.AddMonths(step),
        };
        Refresh();
    }

    /// <summary>The first and last day shown. A month shows whole weeks around it.</summary>
    public (DateOnly From, DateOnly To) Range()
    {
        switch (Mode)
        {
            case CalendarMode.Day:
                return (Anchor, Anchor);
            case CalendarMode.Week:
                var start = StartOfWeek(Anchor);
                return (start, start.AddDays(6));
            default:
                var first = new DateOnly(Anchor.Year, Anchor.Month, 1);
                var last = first.AddMonths(1).AddDays(-1);
                return (StartOfWeek(first), StartOfWeek(last).AddDays(6));
        }
    }

    private static DateOnly StartOfWeek(DateOnly day)
    {
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return day.AddDays(-(((int)day.DayOfWeek - first + 7) % 7));
    }

    public void Refresh()
    {
        var (from, to) = Range();
        var today = _session.Today;
        Heading = Mode switch
        {
            CalendarMode.Day => Anchor.ToString("dddd d MMM yyyy", CultureInfo.CurrentCulture),
            CalendarMode.Week => $"Week of {from.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}",
            _ => Anchor.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        };

        var entries = _session.Calendar.Between(_household(), _me.Id, from, to).ToLookup(entry => entry.Day);
        var mine = _session.Events.VisibleTo(_me.Id, from, to).Where(found => found.OwnerId == _me.Id).Select(found => found.Id).ToHashSet();
        Days.Clear();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var shown = day;
            Days.Add(new CalendarDayViewModel(
                day,
                today,
                Mode != CalendarMode.Month || (day.Month == Anchor.Month),
                entries[day].Select(entry => new CalendarEntryViewModel(entry, mine.Contains(entry.SourceId) ? EditEvent : null)).ToList(),
                () =>
                {
                    Anchor = shown;
                    Mode = CalendarMode.Day;
                }));
        }
    }

    // The event form.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial bool IsFormOpen { get; private set; }

    public string SaveLabel => IsEditing ? "Save changes" : "Add to calendar";

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimes))]
    public partial bool IsAllDay { get; set; }

    public bool HasTimes => !IsAllDay;

    [ObservableProperty]
    public partial TimeSpan? StartTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EndTime { get; set; }

    [ObservableProperty]
    public partial string? Location { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    /// <summary>Time set aside rather than something happening.</summary>
    [ObservableProperty]
    public partial bool IsTimeBlock { get; set; }

    [ObservableProperty]
    public partial bool IsShared { get; set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [RelayCommand]
    private void NewEvent()
    {
        ClearForm();
        StartDate = Anchor.ToDateTime(TimeOnly.MinValue);
        StartTime = new TimeSpan(9, 0, 0);
        EndTime = new TimeSpan(10, 0, 0);
        IsFormOpen = true;
    }

    private void EditEvent(Guid id)
    {
        if (_session.Events.Get(id) is not { } found || found.OwnerId != _me.Id)
        {
            return;
        }

        _editing = found.Id;
        Title = found.Title;
        StartDate = found.StartDate.ToDateTime(TimeOnly.MinValue);
        EndDate = found.EndDate?.ToDateTime(TimeOnly.MinValue);
        IsAllDay = found.IsAllDay;
        StartTime = found.StartTime?.ToTimeSpan();
        EndTime = found.EndTime?.ToTimeSpan();
        Location = found.Location;
        Notes = found.Notes;
        IsTimeBlock = found.Kind == EventKind.TimeBlock;
        IsShared = found.Tier <= PrivacyTier.Household;
        IsEditing = true;
        IsFormOpen = true;
    }

    [RelayCommand]
    private void Save()
    {
        if (StartDate is not { } start)
        {
            Message = "Pick the day it starts.";
            return;
        }

        try
        {
            var calendarEvent = new CalendarEvent
            {
                Id = _editing ?? Guid.NewGuid(),
                HouseholdId = _household().Id,
                OwnerId = _me.Id,
                Title = Title ?? "",
                Kind = IsTimeBlock ? EventKind.TimeBlock : EventKind.Event,
                StartDate = DateOnly.FromDateTime(start),
                StartTime = IsAllDay || StartTime is not { } from ? null : TimeOnly.FromTimeSpan(from),
                EndDate = EndDate is { } end ? DateOnly.FromDateTime(end) : null,
                EndTime = IsAllDay || StartTime is null || EndTime is not { } until ? null : TimeOnly.FromTimeSpan(until),
                Location = Location,
                Notes = Notes,
                Tier = IsShared ? PrivacyTier.Household : PrivacyTier.Private,
            };
            if (_editing is null)
            {
                _session.Events.Add(calendarEvent);
            }
            else
            {
                _session.Events.Update(calendarEvent);
            }

            Anchor = calendarEvent.StartDate;
            ClearForm();
            Refresh();
            _changed();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (_editing is { } id)
        {
            _session.Events.Delete(id);
            ClearForm();
            Refresh();
            _changed();
        }
    }

    [RelayCommand]
    private void CancelEdit() => ClearForm();

    private void ClearForm()
    {
        _editing = null;
        Title = null;
        StartDate = null;
        EndDate = null;
        IsAllDay = false;
        StartTime = null;
        EndTime = null;
        Location = null;
        Notes = null;
        IsTimeBlock = false;
        IsShared = false;
        Message = null;
        IsEditing = false;
        IsFormOpen = false;
    }
}

/// <summary>One day: its heading and what's on it.</summary>
public sealed class CalendarDayViewModel(DateOnly day, DateOnly today, bool inPeriod, IReadOnlyList<CalendarEntryViewModel> entries, Action open)
{
    public DateOnly Date { get; } = day;

    /// <summary>"Wed 21 Oct".</summary>
    public string Heading { get; } = day == today ? $"Today · {day.ToString("ddd d MMM", CultureInfo.CurrentCulture)}" : day.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    public string Number { get; } = day.Day.ToString(CultureInfo.CurrentCulture);

    public bool IsToday { get; } = day == today;

    /// <summary>False for the days before and after the month in the month grid.</summary>
    public bool InPeriod { get; } = inPeriod;

    public double Opacity => InPeriod ? 1 : 0.45;

    public IReadOnlyList<CalendarEntryViewModel> Entries { get; } = entries;

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>The first few, for the month grid.</summary>
    public IReadOnlyList<CalendarEntryViewModel> FirstEntries => Entries.Take(3).ToList();

    public string? MoreText => Entries.Count > 3 ? $"+{Entries.Count - 3} more" : null;

    public IRelayCommand OpenCommand { get; } = new RelayCommand(open);
}

/// <summary>"14:05 · Vet" with what it is.</summary>
public sealed class CalendarEntryViewModel
{
    public CalendarEntryViewModel(CalendarEntry entry, Action<Guid>? edit)
    {
        Title = entry.Title;
        TimeText = entry switch
        {
            { Start: { } start, End: { } end } => $"{start:HH:mm}–{end:HH:mm}",
            { Start: { } start } => start.ToString("HH:mm", CultureInfo.InvariantCulture),
            { End: { } end } => $"until {end:HH:mm}",
            _ => "",
        };
        KindText = entry.Kind switch
        {
            EntryKind.Task => "Task",
            EntryKind.Chore => "Chore",
            EntryKind.Event => "Event",
            EntryKind.TimeBlock => "Time block",
            EntryKind.Upkeep => "Upkeep",
            EntryKind.Warranty => "Warranty",
            EntryKind.Meal => "Meal",
            _ => "",
        };
        Detail = entry.Detail;
        Colour = Avalonia.Media.SolidColorBrush.Parse(entry.Kind switch
        {
            EntryKind.Task => "#3B82F6",
            EntryKind.Chore => "#10B981",
            EntryKind.Event => "#8B5CF6",
            EntryKind.TimeBlock => "#F59E0B",
            EntryKind.Upkeep => "#EF4444",
            EntryKind.Warranty => "#EF4444",
            _ => "#6B7280",
        });
        CanEdit = edit is not null;
        EditCommand = new RelayCommand(() => edit?.Invoke(entry.SourceId), () => edit is not null);
    }

    public string Title { get; }

    public string TimeText { get; }

    public bool HasTime => TimeText.Length > 0;

    public string KindText { get; }

    public string? Detail { get; }

    /// <summary>A stripe beside it, by kind, so a glance tells tasks from events.</summary>
    public Avalonia.Media.IBrush Colour { get; }

    public bool CanEdit { get; }

    public IRelayCommand EditCommand { get; }
}
