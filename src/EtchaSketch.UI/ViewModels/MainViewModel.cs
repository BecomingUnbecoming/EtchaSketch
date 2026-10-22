using System;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.UI.Services;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// Opens the app's data and decides which screen to show: household setup on first launch, the
/// profile picker when nobody has chosen who they are on this device, otherwise their home screen,
/// behind their PIN if they set one. Also locks a profile again when the app sits unused.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>How long the app can sit unused before a profile with a PIN locks again.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long someone can be away in another app before coming back needs the PIN.</summary>
    public static readonly TimeSpan AwayLimit = TimeSpan.FromMinutes(1);

    private readonly AppSession? _session;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset? _awaySince;

    /// <summary>Used by the designer preview, which has no data to open.</summary>
    public MainViewModel()
        : this(App.Environment)
    {
    }

    public MainViewModel(AppEnvironment? environment)
    {
        if (environment is null)
        {
            CurrentPage = new MessageViewModel(
                "EtchaSketch can't run here yet",
                "This system has no secure place to keep the app's key, so your data can't be opened.");
            return;
        }

        try
        {
            _session = AppSession.Open(environment);
        }
        catch (Exception exception) when (exception is DatabaseKeyMissingException or SqliteException
                                              or InvalidOperationException or PlatformNotSupportedException)
        {
            CurrentPage = new MessageViewModel("Your data couldn't be opened", exception.Message);
            return;
        }

        _lastActivity = _session.Now;
        CurrentPage = StartPage();
        ReminderActions.Requested += OnReminderActionRequested;
    }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; private set; }

    public void Dispose()
    {
        ReminderActions.Requested -= OnReminderActionRequested;
        _session?.Dispose();
    }

    // "Done" pressed on a phone notification. A locked or unchosen profile waits: its home screen
    // takes the request when it opens.
    private void OnReminderActionRequested() => Dispatcher.UIThread.Post(() => (CurrentPage as HomeViewModel)?.CompleteRequestedTasks());

    /// <summary>Someone touched or typed in the app. The views report this so idle time can be measured.</summary>
    public void NoteActivity()
    {
        if (_session is not null)
        {
            _lastActivity = _session.Now;
        }
    }

    /// <summary>Locks the open profile if the app has gone unused for <see cref="IdleLimit"/>. The view checks this on a timer.</summary>
    public void LockIfIdle()
    {
        if (_session is not null && _session.Now - _lastActivity >= IdleLimit)
        {
            LockNow();
        }
    }

    /// <summary>The app went into the background, such as switching to another app on a phone.</summary>
    public void EnteredBackground() => _awaySince ??= _session?.Now;

    /// <summary>The app is back on screen. Locks the open profile if it was away for longer than <see cref="AwayLimit"/>.</summary>
    public void ReturnedToForeground()
    {
        if (_session is not null && _awaySince is { } since && _session.Now - since > AwayLimit)
        {
            LockNow();
        }

        _awaySince = null;
        NoteActivity();
        (CurrentPage as HomeViewModel)?.CompleteRequestedTasks();
    }

    /// <summary>Shows the lock screen if the open profile has a PIN. Does nothing otherwise.</summary>
    public void LockNow()
    {
        if (CurrentPage is HomeViewModel home && _session!.Locks.HasPin(home.Me.Id))
        {
            CurrentPage = Unlock(home.Household, home.Me);
        }
    }

    private ViewModelBase StartPage()
    {
        var session = _session!;
        if (session.Households.CurrentProfileId() is { } profileId && session.Households.HouseholdOf(profileId) is { } household)
        {
            return Open(household, household.Members.Single(member => member.Id == profileId));
        }

        return session.Households.All().Count == 0
            ? new SetupViewModel(session, ShowHome)
            : Picker();
    }

    /// <summary>A person's home screen, or the lock screen first if they set a PIN.</summary>
    private ViewModelBase Open(Household household, Profile person) =>
        _session!.Locks.HasPin(person.Id) ? Unlock(household, person) : Home(household, person);

    private UnlockViewModel Unlock(Household household, Profile person) => new(_session!, household, person, ShowHome, ShowPicker);

    private HomeViewModel Home(Household household, Profile me) => new(_session!, household, me, ShowPicker, LockNow);

    private ProfilePickerViewModel Picker() => new(_session!, (household, person) => CurrentPage = Open(household, person));

    private void ShowHome(Household household, Profile me)
    {
        NoteActivity();
        CurrentPage = Home(household, me);
    }

    private void ShowPicker() => CurrentPage = Picker();
}
