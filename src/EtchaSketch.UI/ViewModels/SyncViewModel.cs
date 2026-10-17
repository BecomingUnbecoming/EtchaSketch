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
        Update();
    }

    [ObservableProperty]
    public partial string ServerAddress { get; set; }

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

    /// <summary>One "Add a device" button per person, once sync is on.</summary>
    public ObservableCollection<PersonInvite> Invites { get; } = [];

    [RelayCommand]
    private async Task TurnOn()
    {
        Uri? address = null;
        await Run(async () =>
        {
            address = SyncAddress.Parse(ServerAddress);
            await _session.TurnOnSyncAsync(address, _householdId);
        }, () => address);
    }

    [RelayCommand]
    private Task SyncNow() => Run(async () =>
    {
        if (await _session.SyncAsync() is { } result)
        {
            _synced(result);
        }
    }, () => _session.SyncServerAddress);

    [RelayCommand]
    private void Close() => _close();

    private Task Invite(Profile person) => Run(async () =>
    {
        PairingCode = await _session.Sync!.InviteAsync(person.Id);
        PairingHint = $"Enter this code on {person.DisplayName}'s device within 15 minutes. It works once.";
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

    private void Update()
    {
        IsEnabled = _session.IsSyncOn;
        StatusText = !IsEnabled
            ? "Sync is off. Your data stays on this device."
            : _session.LastSynced is { } at
                ? $"Synced at {at.ToString("HH:mm", CultureInfo.CurrentCulture)}"
                : "Not synced yet";

        Invites.Clear();
        if (IsEnabled && _session.Households.Get(_householdId) is { } household)
        {
            foreach (var person in household.Members)
            {
                Invites.Add(new PersonInvite(person.DisplayName, new AsyncRelayCommand(() => Invite(person))));
            }
        }
    }
}

public sealed record PersonInvite(string Name, IAsyncRelayCommand InviteCommand);
