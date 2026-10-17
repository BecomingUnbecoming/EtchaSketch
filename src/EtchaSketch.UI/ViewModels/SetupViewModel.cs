using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>First launch: name the household and the people in it.</summary>
public partial class SetupViewModel(AppSession session, Action<Household, Profile> done) : ViewModelBase
{
    [ObservableProperty]
    public partial string HouseholdName { get; set; } = "Home";

    [ObservableProperty]
    public partial string YourName { get; set; } = "";

    /// <summary>Optionally, one more person at home. More can be added later.</summary>
    [ObservableProperty]
    public partial string? OtherName { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>To join a household that already syncs: its server's address…</summary>
    [ObservableProperty]
    public partial string JoinAddress { get; set; } = "";

    /// <summary>…and a pairing code made for you on one of its devices.</summary>
    [ObservableProperty]
    public partial string JoinCode { get; set; } = "";

    [ObservableProperty]
    public partial string? JoinErrorMessage { get; private set; }

    [RelayCommand]
    private async Task Join()
    {
        Uri? address = null;
        JoinErrorMessage = null;
        try
        {
            address = SyncAddress.Parse(JoinAddress);
            var profileId = await session.JoinAsync(address, JoinCode);
            var household = session.Households.HouseholdOf(profileId)!;
            done(household, household.Members.Single(member => member.Id == profileId));
        }
        catch (Exception exception) when (Messages.IsSyncProblem(exception))
        {
            JoinErrorMessage = Messages.ForSync(exception, address);
        }
    }

    [RelayCommand]
    private void Create()
    {
        try
        {
            var household = session.Households.Create(HouseholdName, YourName);
            if (!string.IsNullOrWhiteSpace(OtherName))
            {
                session.Households.AddMember(household.Id, OtherName);
            }

            var me = household.Members[0];
            session.Households.SetCurrentProfile(me.Id);
            done(session.Households.Get(household.Id)!, me);
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = Messages.Friendly(exception);
        }
    }
}
