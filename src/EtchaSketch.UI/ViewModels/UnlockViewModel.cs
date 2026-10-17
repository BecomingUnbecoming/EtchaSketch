using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>Asks for a person's PIN, or their device's unlock, before showing their profile.</summary>
public partial class UnlockViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Household _household;
    private readonly Profile _person;
    private readonly Action<Household, Profile> _unlocked;
    private readonly Action _switchPerson;

    public UnlockViewModel(AppSession session, Household household, Profile person, Action<Household, Profile> unlocked, Action switchPerson)
    {
        _session = session;
        _household = household;
        _person = person;
        _unlocked = unlocked;
        _switchPerson = switchPerson;
        CanUseDeviceUnlock = session.HasDeviceUnlock && session.Locks.UsesDeviceUnlock(person.Id);
    }

    public string Name => _person.DisplayName;

    public bool CanUseDeviceUnlock { get; }

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [RelayCommand]
    private void Unlock()
    {
        var check = _session.Locks.Check(_person.Id, Pin, _session.Now);
        Pin = "";
        switch (check.Outcome)
        {
            case PinOutcome.Correct:
                _unlocked(_household, _person);
                break;
            case PinOutcome.Wrong:
                ErrorMessage = check.TriesLeft == 1
                    ? "That PIN isn't right. 1 more try before a short wait."
                    : $"That PIN isn't right. {check.TriesLeft} more tries before a short wait.";
                break;
            default:
                ErrorMessage = $"Too many wrong tries. Try again in {Messages.Duration(check.TryAgainAt!.Value - _session.Now)}.";
                break;
        }
    }

    [RelayCommand]
    private async Task DeviceUnlock()
    {
        if (await _session.DeviceUnlock!.ConfirmAsync($"Unlock {_person.DisplayName}'s profile"))
        {
            _session.Locks.UnlockedAnotherWay(_person.Id);
            _unlocked(_household, _person);
        }
    }

    [RelayCommand]
    private void SwitchPerson() => _switchPerson();
}
