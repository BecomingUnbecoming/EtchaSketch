using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Pantry;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>The household's stock: what there is, where, how much, and what's about to go off or run out.</summary>
public partial class PantryViewModel(AppSession session, Household household, Action changed) : ViewModelBase
{
    private Guid? _editing;

    public ObservableCollection<StockRowViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial decimal? Amount { get; set; } = 1;

    [ObservableProperty]
    public partial string? Unit { get; set; }

    [ObservableProperty]
    public partial string? Location { get; set; }

    [ObservableProperty]
    public partial DateTime? BestBefore { get; set; }

    /// <summary>At or below this it counts as running low. Empty never runs low.</summary>
    [ObservableProperty]
    public partial decimal? LowAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    public partial bool IsEditing { get; private set; }

    public string SaveLabel => IsEditing ? "Save changes" : "Add to stock";

    [ObservableProperty]
    public partial string? Message { get; private set; }

    public void Refresh()
    {
        Items.Clear();
        var today = session.Today;
        foreach (var item in session.Pantry.Stock(household.Id))
        {
            Items.Add(new StockRowViewModel(item, today, Use, Edit, Delete, AddToList));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var item = new StockItem
            {
                Id = _editing ?? Guid.NewGuid(),
                HouseholdId = household.Id,
                Name = Name ?? "",
                Amount = Amount ?? 0,
                Unit = Unit,
                Location = Location,
                BestBefore = BestBefore is { } date ? DateOnly.FromDateTime(date) : null,
                LowAt = LowAt,
                Barcode = _editing is { } id ? session.Pantry.GetStock(id)?.Barcode : null,
            };
            if (_editing is null)
            {
                session.Pantry.AddStock(item);
            }
            else
            {
                session.Pantry.UpdateStock(item);
            }

            Message = null;
            ClearForm();
            changed();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void CancelEdit() => ClearForm();

    [RelayCommand]
    private void AddLowToList()
    {
        var added = session.Pantry.AddLowToList(household.Id);
        Message = added switch
        {
            0 => "Nothing new is running low.",
            1 => "Added 1 thing to the shopping list.",
            _ => $"Added {added} things to the shopping list.",
        };
        changed();
    }

    private void Use(StockItem item)
    {
        session.Pantry.Use(item.Id, 1);
        changed();
    }

    private void Edit(StockItem item)
    {
        _editing = item.Id;
        Name = item.Name;
        Amount = item.Amount;
        Unit = item.Unit;
        Location = item.Location;
        BestBefore = item.BestBefore?.ToDateTime(TimeOnly.MinValue);
        LowAt = item.LowAt;
        IsEditing = true;
    }

    private void Delete(StockItem item)
    {
        session.Pantry.DeleteStock(item.Id);
        changed();
    }

    private void AddToList(StockItem item)
    {
        session.Pantry.AddToList(new ShoppingItem { HouseholdId = household.Id, Name = item.Name, Unit = item.Unit, StockItemId = item.Id });
        Message = $"{item.Name} is on the shopping list.";
        changed();
    }

    private void ClearForm()
    {
        _editing = null;
        Name = null;
        Amount = 1;
        Unit = null;
        Location = null;
        BestBefore = null;
        LowAt = null;
        IsEditing = false;
    }
}

/// <summary>One stock item, like "Milk · 2 bottles · Fridge · goes off Thu 23 Oct".</summary>
public sealed class StockRowViewModel
{
    public StockRowViewModel(StockItem item, DateOnly today, Action<StockItem> use, Action<StockItem> edit, Action<StockItem> delete, Action<StockItem> addToList)
    {
        Name = item.Name;
        AmountText = $"{item.Amount.ToString("0.##", CultureInfo.CurrentCulture)}{(item.Unit is { } unit ? " " + unit : "")}";
        IsLow = item.IsLow;
        IsExpiring = item.Amount > 0 && item.BestBefore is { } date && date <= today.AddDays(PantryAlerts.DaysAhead);
        var details = new[]
        {
            item.Location,
            item.BestBefore is { } best
                ? best < today ? "past its best-before date"
                : best == today ? "goes off today"
                : $"goes off {best.ToString("ddd d MMM", CultureInfo.CurrentCulture)}"
                : null,
            IsLow ? "running low" : null,
        };
        Details = string.Join(" · ", details.OfType<string>());
        UseCommand = new RelayCommand(() => use(item), () => item.Amount > 0);
        EditCommand = new RelayCommand(() => edit(item));
        DeleteCommand = new RelayCommand(() => delete(item));
        AddToListCommand = new RelayCommand(() => addToList(item));
    }

    public string Name { get; }

    public string AmountText { get; }

    public string Details { get; }

    public bool IsLow { get; }

    public bool IsExpiring { get; }

    /// <summary>Takes one out of stock.</summary>
    public IRelayCommand UseCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand AddToListCommand { get; }
}
