using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>"Who's using EtchaSketch?" Lets people share a device, and adds new people to the household.</summary>
public partial class ProfilePickerViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Action<Household, Profile> _chosen;

    public ProfilePickerViewModel(AppSession session, Action<Household, Profile> chosen)
    {
        _session = session;
        _chosen = chosen;
        Load();
    }

    public ObservableCollection<PersonChoice> People { get; } = [];

    [ObservableProperty]
    public partial string NewPersonName { get; set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [RelayCommand]
    private void AddPerson()
    {
        try
        {
            var person = _session.Households.AddMember(_session.Households.All()[0].Id, NewPersonName);
            _session.Sync?.AddPerson(person.Id);
            NewPersonName = "";
            ErrorMessage = null;
            Load();
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = Messages.Friendly(exception);
        }
    }

    private void Load()
    {
        People.Clear();
        foreach (var household in _session.Households.All())
        {
            foreach (var person in household.Members)
            {
                People.Add(new PersonChoice(person.DisplayName, new RelayCommand(() => Choose(household, person))));
            }
        }
    }

    private void Choose(Household household, Profile person)
    {
        _session.Households.SetCurrentProfile(person.Id);
        _chosen(household, household.Members.Single(member => member.Id == person.Id));
    }
}

public sealed record PersonChoice(string Name, IRelayCommand ChooseCommand);
