using System;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly Household _household;
    private readonly Profile _me;
    private readonly Action _switchPerson;

    public HomeViewModel(AppSession session, Household household, Profile me, Action switchPerson)
    {
        _session = session;
        _household = household;
        _me = me;
        _switchPerson = switchPerson;
        Refresh();
    }

    public string Greeting => $"Hi, {_me.DisplayName}";

    /// <summary>Your own tasks plus everyone's shared chores.</summary>
    public ObservableCollection<TaskRowViewModel> MyTasks { get; } = [];

    public ObservableCollection<ChoreColumnViewModel> ChoreColumns { get; } = [];

    /// <summary>The task being added or edited, or null when the editor is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial TaskEditorViewModel? Editor { get; private set; }

    public bool IsEditing => Editor is not null;

    [RelayCommand]
    private void NewTask() => Editor = new TaskEditorViewModel(_session, _household, _me, existing: null, CloseEditor);

    [RelayCommand]
    private void SwitchPerson() => _switchPerson();

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
        MyTasks.Clear();
        foreach (var row in _session.Tasks.VisibleTo(_me.Id).Select(Row).OfType<TaskRowViewModel>())
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
