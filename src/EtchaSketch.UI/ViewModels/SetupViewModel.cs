using System;
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
