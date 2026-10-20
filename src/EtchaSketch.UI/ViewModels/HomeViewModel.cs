using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>One person's home screen: their task list and the household's chore board.</summary>
public partial class HomeViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private Household _household;
    private readonly Profile _me;
    private readonly Action _switchPerson;
    private readonly Action _lock;
    private DateOnly _shownDay;

    public HomeViewModel(AppSession session, Household household, Profile me, Action switchPerson, Action lockNow)
    {
        _session = session;
        _household = household;
        _me = me;
        _switchPerson = switchPerson;
        _lock = lockNow;
        CanLock = session.Locks.HasPin(me.Id);
        Refresh();
        CompleteRequestedTasks();
    }

    public string Greeting => $"Hi, {_me.DisplayName}";

    /// <summary>The person whose home screen this is.</summary>
    public Profile Me => _me;

    public Household Household => _household;

    /// <summary>Whether this person has a PIN, so "Lock" has something to ask for.</summary>
    [ObservableProperty]
    public partial bool CanLock { get; private set; }

    /// <summary>The PIN settings panel, or null when it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingLockSettings))]
    public partial LockSettingsViewModel? LockSettings { get; private set; }

    public bool IsShowingLockSettings => LockSettings is not null;

    /// <summary>Narrows your list to tasks whose title, project or labels contain this text.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => Refresh();

    /// <summary>Your own tasks plus everyone's shared chores.</summary>
    public ObservableCollection<TaskRowViewModel> MyTasks { get; } = [];

    public ObservableCollection<ChoreColumnViewModel> ChoreColumns { get; } = [];

    /// <summary>The task being added or edited, or null when the editor is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial TaskEditorViewModel? Editor { get; private set; }

    public bool IsEditing => Editor is not null;

    /// <summary>The notification panel, or null when it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingNotifications))]
    public partial NotificationCenterViewModel? Notifications { get; private set; }

    public bool IsShowingNotifications => Notifications is not null;

    /// <summary>The sync settings panel, or null when it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingSync))]
    public partial SyncViewModel? Sync { get; private set; }

    public bool IsShowingSync => Sync is not null;

    /// <summary>The task import panel, or null when it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingImport))]
    public partial ImportViewModel? Import { get; private set; }

    public bool IsShowingImport => Import is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationsLabel))]
    public partial int UnreadCount { get; private set; }

    public string NotificationsLabel => UnreadCount > 0 ? $"Notifications ({UnreadCount})" : "Notifications";

    [RelayCommand]
    private void NewTask() => Editor = new TaskEditorViewModel(_session, _household, _me, existing: null, CloseEditor);

    [RelayCommand]
    private void SwitchPerson() => _switchPerson();

    [RelayCommand]
    private void Lock() => _lock();

    [RelayCommand]
    private void OpenLockSettings() => LockSettings = new LockSettingsViewModel(_session, _me, CloseLockSettings);

    private void CloseLockSettings()
    {
        LockSettings = null;
        CanLock = _session.Locks.HasPin(_me.Id);
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        CheckReminders();
        Notifications = new NotificationCenterViewModel(_session, _me, CloseNotifications);
        UpdateUnreadCount();
    }

    /// <summary>
    /// Raises any reminders that have come due. The home screen runs this every minute while it is
    /// open, and rebuilds its lists when the day changes so "Today" stays true.
    /// </summary>
    [RelayCommand]
    private void CheckReminders()
    {
        if (_session.Today != _shownDay)
        {
            Refresh();
            return;
        }

        _session.Reminders.Check(_household, _session.Now);
        _session.ScheduleReminders(_household, _me);
        UpdateUnreadCount();
    }

    /// <summary>
    /// Completes tasks whose reminder had "Done" pressed outside the app. A task this person can't
    /// see, or one already finished, is skipped.
    /// </summary>
    public void CompleteRequestedTasks()
    {
        var done = false;
        foreach (var taskId in ReminderActions.TakeCompletions())
        {
            if (_session.Tasks.VisibleTo(_me.Id).Any(task => task.Id == taskId))
            {
                _session.Tasks.Complete(taskId, _me.Id, _session.Now);
                done = true;
            }
        }

        if (done)
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenImport() => Import = new ImportViewModel(_session, _household, _me, imported =>
    {
        Import = null;
        if (imported)
        {
            Refresh();
        }
    }, imported: Refresh);

    [RelayCommand]
    private void OpenSync() => Sync = new SyncViewModel(_session, _household.Id, Synced, () => Sync = null);

    /// <summary>
    /// Syncs quietly in the background. The home screen runs this when it opens and every minute.
    /// A failed sync is retried next time; the sync panel shows what went wrong when asked.
    /// </summary>
    [RelayCommand]
    private async Task AutoSync()
    {
        try
        {
            if (await _session.SyncAsync() is { } result)
            {
                Synced(result);
            }
        }
        catch (Exception exception) when (Messages.IsSyncProblem(exception))
        {
        }
    }

    private void Synced(Data.Sync.SyncResult result)
    {
        if (result.Received > 0)
        {
            Refresh();
        }
    }

    private void CloseNotifications()
    {
        Notifications = null;
        UpdateUnreadCount();
    }

    private void UpdateUnreadCount() => UnreadCount = _session.Notifications.UnreadCount(_me.Id, _session.Now);

    private void CloseEditor(bool saved)
    {
        Editor = null;
        if (saved)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        // Other devices may have added people since this screen opened.
        _household = _session.Households.Get(_household.Id) ?? _household;
        _shownDay = _session.Today;
        _session.Reminders.Check(_household, _session.Now);
        _session.ScheduleReminders(_household, _me);
        UpdateUnreadCount();

        MyTasks.Clear();
        var filter = FilterText.Trim();
        foreach (var row in _session.Tasks.VisibleTo(_me.Id).Select(Row).OfType<TaskRowViewModel>().Where(row => Matches(row, filter)))
        {
            MyTasks.Add(row);
        }

        ChoreColumns.Clear();
        foreach (var column in _session.Tasks.ChoreBoard(_household.Id))
        {
            var name = column.AssigneeId is { } id ? NameOf(id) : "Anyone";
            ChoreColumns.Add(new ChoreColumnViewModel(name, column.Chores.Select(Row).OfType<TaskRowViewModel>().ToList()));
        }
    }

    private static bool Matches(TaskRowViewModel row, string filter) =>
        filter.Length == 0
        || row.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (row.Project?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || row.Labels.Any(label => label.Contains(filter.TrimStart('#'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a row from what the privacy gateway lets this person see, so a screen can't show
    /// someone else's notes even if a task carrying them reaches it.
    /// </summary>
    private TaskRowViewModel? Row(TaskItem task)
    {
        var view = PrivacyGateway.Project(task, Audience.ForProfile(_me.Id));
        if (view is null)
        {
            return null;
        }

        return new TaskRowViewModel(
            task,
            view,
            _session.Today,
            assigneeName: task.AssigneeId is { } id ? NameOf(id) : null,
            canDelete: task.OwnerId == _me.Id,
            complete: () =>
            {
                _session.Tasks.Complete(task.Id, _me.Id, _session.Now);
                Refresh();
            },
            edit: () => Editor = new TaskEditorViewModel(_session, _household, _me, task, CloseEditor),
            delete: () =>
            {
                _session.Tasks.Delete(task.Id);
                Refresh();
            });
    }

    private string NameOf(Guid profileId) =>
        _household.Members.FirstOrDefault(member => member.Id == profileId)?.DisplayName ?? "Someone";
}
