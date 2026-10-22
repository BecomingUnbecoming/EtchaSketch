using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Profiles;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>The household's shared shopping list. Ticking what came from stock puts it back in stock.</summary>
public partial class ShoppingViewModel(AppSession session, Household household, Action changed) : ViewModelBase
{
    public ObservableCollection<ShoppingRowViewModel> Lines { get; } = [];

    public bool IsEmpty => Lines.Count == 0;

    [ObservableProperty]
    public partial string? NewLine { get; set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    public void Refresh()
    {
        Lines.Clear();
        foreach (var line in session.Pantry.ShoppingList(household.Id))
        {
            Lines.Add(new ShoppingRowViewModel(line, Toggle, Delete));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Add()
    {
        try
        {
            session.Pantry.AddToList(new ShoppingItem { HouseholdId = household.Id, Name = NewLine ?? "" });
            NewLine = null;
            Message = null;
            changed();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void ClearBought()
    {
        session.Pantry.ClearBought(household.Id);
        changed();
    }

    private void Toggle(ShoppingItem line)
    {
        if (line.BoughtAt is null)
        {
            session.Pantry.Tick(line.Id, session.Now);
        }
        else
        {
            session.Pantry.Untick(line.Id);
        }

        changed();
    }

    private void Delete(ShoppingItem line)
    {
        session.Pantry.DeleteFromList(line.Id);
        changed();
    }
}

public sealed class ShoppingRowViewModel(ShoppingItem line, Action<ShoppingItem> toggle, Action<ShoppingItem> delete)
{
    public string Name { get; } = line.Name;

    public string? AmountText { get; } = line.Amount is { } amount
        ? $"{amount.ToString("0.##", CultureInfo.CurrentCulture)}{(line.Unit is { } unit ? " " + unit : "")}"
        : null;

    public bool IsBought { get; } = line.BoughtAt is not null;

    public bool FromStock { get; } = line.StockItemId is not null;

    public IRelayCommand ToggleCommand { get; } = new RelayCommand(() => toggle(line));

    public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => delete(line));
}
