using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.UI.Services;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// Opens the app's data and decides which screen to show: household setup on first launch, the
/// profile picker when nobody has chosen who they are on this device, otherwise their home screen.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AppSession? _session;

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

        CurrentPage = StartPage();
    }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; private set; }

    public void Dispose() => _session?.Dispose();

    private ViewModelBase StartPage()
    {
        var session = _session!;
        if (session.Households.CurrentProfileId() is { } profileId && session.Households.HouseholdOf(profileId) is { } household)
        {
            return Home(household, household.Members.Single(member => member.Id == profileId));
        }

        return session.Households.All().Count == 0
            ? new SetupViewModel(session, ShowHome)
            : Picker();
    }

    private HomeViewModel Home(Household household, Profile me) => new(_session!, household, me, ShowPicker);

    private ProfilePickerViewModel Picker() => new(_session!, ShowHome);

    private void ShowHome(Household household, Profile me) => CurrentPage = Home(household, me);

    private void ShowPicker() => CurrentPage = Picker();
}
