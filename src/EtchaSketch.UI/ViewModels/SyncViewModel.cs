using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Sync;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// Sync settings: which server to use, turning sync on, and making pairing codes so other
/// devices can join. The server only ever holds data it can't read.
/// </summary>
public partial class SyncViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Guid _householdId;
    private readonly Action<SyncResult> _synced;
    private readonly Action _close;

    public SyncViewModel(AppSession session, Guid householdId, Action<SyncResult> synced, Action close)
    {
        _session = session;
        _householdId = householdId;
        _synced = synced;
        _close = close;
        ServerAddress = session.SyncServerAddress?.ToString() ?? "";
        JoinToken = session.Sync?.JoinToken ?? "";
        Update();
    }

    [ObservableProperty]
    public partial string ServerAddress { get; set; }

    /// <summary>The server's join token, if its owner set one. It stays on this device and travels to new devices with their code.</summary>
    [ObservableProperty]
    public partial string JoinToken { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEnabled { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPairingCode))]
    public partial string? PairingCode { get; private set; }

    [ObservableProperty]
    public partial string? PairingHint { get; private set; }

    public bool HasPairingCode => PairingCode is not null;

    /// <summary>A passphrase to lock the recovery key with…</summary>
    [ObservableProperty]
    public partial string RecoveryPassphrase { get; set; } = "";

    /// <summary>…and the key it makes, to keep somewhere safe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryKey))]
    public partial string? RecoveryKeyText { get; private set; }

    public bool HasRecoveryKey => RecoveryKeyText is not null;

    /// <summary>One "Add a device" button per person, once sync is on.</summary>
    public ObservableCollection<PersonInvite> Invites { get; } = [];

    /// <summary>Every device that has synced in this household, so a lost one can be removed.</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    /// <summary>What the last removal did, or null.</summary>
    [ObservableProperty]
    public partial string? RemovalMessage { get; private set; }

    [RelayCommand]
    private async Task TurnOn()
    {
        Uri? address = null;
        await Run(async () =>
        {
            address = SyncAddress.Parse(ServerAddress);
            await _session.TurnOnSyncAsync(address, _householdId, JoinToken);
        }, () => address);
    }

    [RelayCommand]
    private Task SaveJoinToken() => Run(async () =>
    {
        _session.SetJoinToken(JoinToken);
        await SyncNow();
    }, () => _session.SyncServerAddress);

    [RelayCommand]
    private Task SyncNow() => Run(async () =>
    {
        if (await _session.SyncAsync() is { } result)
        {
            _synced(result);
        }
    }, () => _session.SyncServerAddress);

    [RelayCommand]
    private Task MakeRecoveryKey() => Run(() =>
    {
        RecoveryKeyText = _session.CreateRecoveryKey(RecoveryPassphrase);
        RecoveryPassphrase = "";
        return Task.CompletedTask;
    }, () => _session.SyncServerAddress);

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private Task InviteAssistant() => Run(async () =>
    {
        PairingCode = await _session.Sync!.InviteAssistantAsync(_householdId);
        PairingHint = "For the household assistant (see docs/assistant.md): run its join command with this code within 15 minutes. "
            + "It gets the household's shared tasks only, never anyone's private ones.";
    }, () => _session.SyncServerAddress);

    private Task Invite(Profile person) => Run(async () =>
    {
        PairingCode = await _session.Sync!.InviteAsync(person.Id);
        PairingHint = $"Enter this code on {person.DisplayName}'s device within 15 minutes. It works once.";
    }, () => _session.SyncServerAddress);

    private Task Remove(KnownDevice device) => Run(async () =>
    {
        var removal = await _session.Sync!.RemoveDeviceAsync(device.Id);
        RemovalMessage = $"{device.Name} is removed and can't sync any more. It keeps what it had already downloaded, protected only by its PIN. "
            + "Make a new recovery key below, so your key keeps working with this change."
            + (removal.StreamsNotHeld > 0
                ? $" Its owner's private tasks are kept with keys only their own devices hold, so remove it from one of their devices too."
                : "");
    }, () => _session.SyncServerAddress);

    private async Task Run(Func<Task> action, Func<Uri?> address)
    {
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (Exception exception) when (Messages.IsSyncProblem(exception))
        {
            ErrorMessage = Messages.ForSync(exception, address());
        }

        Update();
    }

    private string Day(DateTimeOffset time) => _session.Local(time).ToString("d MMM", CultureInfo.CurrentCulture);

    private void Update()
    {
        IsEnabled = _session.IsSyncOn;
        StatusText = !IsEnabled
            ? "Sync is off. Your data stays on this device."
            : _session.LastSynced is { } at
                ? $"Synced at {at.ToString("HH:mm", CultureInfo.CurrentCulture)}"
                : "Not synced yet";

        Invites.Clear();
        Devices.Clear();
        if (IsEnabled && _session.Households.Get(_householdId) is { } household)
        {
            foreach (var person in household.Members)
            {
                Invites.Add(new PersonInvite(person.DisplayName, new AsyncRelayCommand(() => Invite(person))));
            }

            foreach (var device in _session.Sync!.Devices(_householdId))
            {
                var owner = household.Members.FirstOrDefault(member => member.Id == device.ProfileId)?.DisplayName;
                var details = device.RemovedAt is { } removed ? $"Removed {Day(removed)}"
                    : device.IsThisDevice ? "This device"
                    : $"{(owner is null ? "" : owner + "'s · ")}added {Day(device.AddedAt)}";
                Devices.Add(new DeviceRowViewModel(device.Name, details, !device.IsThisDevice && device.RemovedAt is null, () => Remove(device)));
            }
        }
    }
}

public sealed record PersonInvite(string Name, IAsyncRelayCommand InviteCommand);

/// <summary>One device in the household's list, with a two-step Remove.</summary>
public sealed partial class DeviceRowViewModel(string name, string details, bool canRemove, Func<Task> remove) : ViewModelBase
{
    public string Name { get; } = name;

    /// <summary>Like "Kai's · added 21 Oct", "This device", or "Removed 22 Oct".</summary>
    public string Details { get; } = details;

    public bool CanRemove { get; } = canRemove;

    /// <summary>Remove was pressed once; the row now asks to confirm.</summary>
    [ObservableProperty]
    public partial bool IsConfirming { get; private set; }

    [RelayCommand]
    private void AskToRemove() => IsConfirming = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirming = false;

    [RelayCommand]
    private Task ConfirmRemove()
    {
        IsConfirming = false;
        return remove();
    }
}

