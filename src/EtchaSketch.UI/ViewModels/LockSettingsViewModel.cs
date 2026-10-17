using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// Setting, changing or removing your PIN on this device, and whether the device's own unlock can
/// open your profile too. Changing or removing a PIN needs the current one.
/// </summary>
public partial class LockSettingsViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Profile _me;
    private readonly Action _close;

    public LockSettingsViewModel(AppSession session, Profile me, Action close)
    {
        _session = session;
        _me = me;
        _close = close;
        Update();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseDeviceUnlock))]
    public partial bool HasPin { get; private set; }

    /// <summary>Whether to offer the device's unlock: the device has one, and there's a PIN to fall back on.</summary>
    public bool CanUseDeviceUnlock => HasPin && _session.HasDeviceUnlock;

    [ObservableProperty]
    public partial bool UseDeviceUnlock { get; set; }

    [ObservableProperty]
    public partial string CurrentPin { get; set; } = "";

    [ObservableProperty]
    public partial string NewPin { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPin { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    partial void OnUseDeviceUnlockChanged(bool value)
    {
        if (HasPin && value != _session.Locks.UsesDeviceUnlock(_me.Id))
        {
            _session.Locks.SetDeviceUnlock(_me.Id, value);
        }
    }

    [RelayCommand]
    private void SavePin()
    {
        if (NewPin != ConfirmPin)
        {
            ErrorMessage = "The two PINs don't match.";
            return;
        }

        if (!CurrentPinIsRight())
        {
            return;
        }

        try
        {
            _session.Locks.SetPin(_me.Id, NewPin);
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = Messages.Friendly(exception);
            return;
        }

        Done();
    }

    [RelayCommand]
    private void RemovePin()
    {
        if (CurrentPinIsRight())
        {
            _session.Locks.RemovePin(_me.Id);
            Done();
        }
    }

    [RelayCommand]
    private void Close() => _close();

    private bool CurrentPinIsRight()
    {
        if (!HasPin)
        {
            return true;
        }

        var check = _session.Locks.Check(_me.Id, CurrentPin, _session.Now);
        CurrentPin = "";
        ErrorMessage = check.Outcome switch
        {
            PinOutcome.Correct => null,
            PinOutcome.Wrong => "Your current PIN isn't right.",
            _ => $"Too many wrong tries. Try again in {Messages.Duration(check.TryAgainAt!.Value - _session.Now)}.",
        };
        return ErrorMessage is null;
    }

    private void Done()
    {
        ErrorMessage = null;
        CurrentPin = NewPin = ConfirmPin = "";
        Update();
    }

    private void Update()
    {
        HasPin = _session.Locks.HasPin(_me.Id);
        UseDeviceUnlock = _session.Locks.UsesDeviceUnlock(_me.Id);
        StatusText = HasPin
            ? "Your profile is locked with a PIN on this device. EtchaSketch asks for it when it opens, after 5 minutes without use, and when you come back after leaving the app for a minute."
            : "No PIN yet. Anyone using this device can open your profile.";
    }
}
