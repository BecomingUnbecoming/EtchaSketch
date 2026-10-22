using System;
using System.Collections.Generic;
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

/// <summary>The household's things: what, where, warranty, and who has it right now.</summary>
public partial class InventoryViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Household _household;
    private readonly Action _changed;
    private Guid? _editing;

    // The one item opened from its label, until "Show everything".
    private Guid? _only;

    public InventoryViewModel(AppSession session, Household household, Action changed)
    {
        _session = session;
        _household = household;
        _changed = changed;
        WhoHasItOptions = [new AssigneeOption("In its place", null), .. household.Members.Select(m => new AssigneeOption(m.DisplayName, m.Id))];
        WhoHasIt = WhoHasItOptions[0];
    }

    public ObservableCollection<InventoryRowViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Narrows the list to things whose name, place, category, serial or notes contain this text.</summary>
    [ObservableProperty]
    public partial string? Search { get; set; }

    partial void OnSearchChanged(string? value) => Refresh();

    [ObservableProperty]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial string? Location { get; set; }

    [ObservableProperty]
    public partial string? Category { get; set; }

    [ObservableProperty]
    public partial string? Serial { get; set; }

    [ObservableProperty]
    public partial DateTime? PurchasedOn { get; set; }

    [ObservableProperty]
    public partial decimal? Price { get; set; }

    [ObservableProperty]
    public partial DateTime? WarrantyEnds { get; set; }

    [ObservableProperty]
    public partial string? Manual { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    /// <summary>A vehicle keeps a distance in its log, and its upkeep can be due by distance.</summary>
    [ObservableProperty]
    public partial bool IsVehicle { get; set; }

    public IReadOnlyList<string> DistanceUnits { get; } = ["km", "mi"];

    [ObservableProperty]
    public partial string DistanceUnit { get; set; } = "km";

    /// <summary>One thing's upkeep and log, or null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingDetail))]
    public partial ItemDetailViewModel? Detail { get; private set; }

    public bool IsShowingDetail => Detail is not null;

    /// <summary>The Homebox / LubeLogger import panel, or null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting))]
    public partial ThingsImportViewModel? Importer { get; private set; }

    public bool IsImporting => Importer is not null;

    [RelayCommand]
    private void OpenImport() => Importer = new ThingsImportViewModel(_session, _household, _changed, () => Importer = null);

    public IReadOnlyList<AssigneeOption> WhoHasItOptions { get; }

    [ObservableProperty]
    public partial AssigneeOption WhoHasIt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    public partial bool IsEditing { get; private set; }

    public string SaveLabel => IsEditing ? "Save changes" : "Add it";

    /// <summary>Whether the add or edit form is showing. It folds away when not in use, since it's long on a phone.</summary>
    [ObservableProperty]
    public partial bool IsFormOpen { get; private set; }

    [RelayCommand]
    private void OpenForm() => IsFormOpen = true;

    /// <summary>Whether the list shows one item opened from its label.</summary>
    [ObservableProperty]
    public partial bool IsShowingOne { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    public void Refresh()
    {
        Items.Clear();
        var today = _session.Today;
        var items = _only is { } id
            ? _session.Inventory.Items(_household.Id).Where(item => item.Id == id)
            : _session.Inventory.Items(_household.Id, Search);
        foreach (var item in items)
        {
            Items.Add(new InventoryRowViewModel(item, today, NameOf, Edit, Delete, ReturnIt, ShowDetail));
        }

        Detail?.Refresh();

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Shows one item, as when its QR label is scanned. Returns false when it isn't in this
    /// household (deleted, or a label from somewhere else).
    /// </summary>
    public bool Open(Guid itemId)
    {
        if (_session.Inventory.Get(itemId) is not { } item || item.HouseholdId != _household.Id)
        {
            Message = "That label is for something that isn't in this household's inventory.";
            return false;
        }

        _only = item.Id;
        IsShowingOne = true;
        Message = null;
        Refresh();
        return true;
    }

    public void LabelSaved(string name, string fileName) =>
        Message = $"Saved the label for {name} as {fileName}. Print it and stick it on.";

    public void LabelFailed(string reason) => Message = $"The label couldn't be saved: {reason}";

    [RelayCommand]
    private void ShowEverything()
    {
        _only = null;
        IsShowingOne = false;
        Refresh();
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var item = new InventoryItem
            {
                Id = _editing ?? Guid.NewGuid(),
                HouseholdId = _household.Id,
                Name = Name ?? "",
                Location = Location,
                Category = Category,
                Serial = Serial,
                PurchasedOn = PurchasedOn is { } bought ? DateOnly.FromDateTime(bought) : null,
                Price = Price,
                WarrantyEnds = WarrantyEnds is { } ends ? DateOnly.FromDateTime(ends) : null,
                Manual = Manual,
                Notes = Notes,
                BorrowedBy = WhoHasIt.ProfileId,
                DistanceUnit = IsVehicle ? DistanceUnit : null,
            };
            if (_editing is null)
            {
                _session.Inventory.Add(item);
            }
            else
            {
                _session.Inventory.Update(item);
            }

            Message = null;
            ClearForm();
            _changed();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void CancelEdit() => ClearForm();

    private void Edit(InventoryItem item)
    {
        _editing = item.Id;
        Name = item.Name;
        Location = item.Location;
        Category = item.Category;
        Serial = item.Serial;
        PurchasedOn = item.PurchasedOn?.ToDateTime(TimeOnly.MinValue);
        Price = item.Price;
        WarrantyEnds = item.WarrantyEnds?.ToDateTime(TimeOnly.MinValue);
        Manual = item.Manual;
        Notes = item.Notes;
        WhoHasIt = WhoHasItOptions.FirstOrDefault(option => option.ProfileId == item.BorrowedBy) ?? WhoHasItOptions[0];
        IsVehicle = item.IsVehicle;
        DistanceUnit = item.DistanceUnit ?? "km";
        IsEditing = true;
        IsFormOpen = true;
    }

    private void Delete(InventoryItem item)
    {
        _session.Inventory.Delete(item.Id);
        if (_only == item.Id)
        {
            ShowEverything();
        }

        if (_editing == item.Id)
        {
            ClearForm();
        }

        if (Detail?.ItemId == item.Id)
        {
            Detail = null;
        }

        _changed();
    }

    private void ReturnIt(InventoryItem item)
    {
        _session.Inventory.Update(item with { BorrowedBy = null });
        Message = $"{item.Name} is back in its place.";
        _changed();
    }

    private void ShowDetail(InventoryItem item) => Detail = new ItemDetailViewModel(_session, item, _changed, () => Detail = null);

    private string NameOf(Guid profileId) =>
        _household.Members.FirstOrDefault(member => member.Id == profileId)?.DisplayName ?? "Someone";

    private void ClearForm()
    {
        _editing = null;
        Name = null;
        Location = null;
        Category = null;
        Serial = null;
        PurchasedOn = null;
        Price = null;
        WarrantyEnds = null;
        Manual = null;
        Notes = null;
        WhoHasIt = WhoHasItOptions[0];
        IsVehicle = false;
        DistanceUnit = "km";
        IsEditing = false;
        IsFormOpen = false;
    }
}

/// <summary>One thing the household owns, like "Drill · Garage · Tools · with Kai".</summary>
public sealed class InventoryRowViewModel
{
    public InventoryRowViewModel(InventoryItem item, DateOnly today, Func<Guid, string> nameOf,
        Action<InventoryItem> edit, Action<InventoryItem> delete, Action<InventoryItem> returnIt, Action<InventoryItem> showDetail)
    {
        Item = item;
        Name = item.Name;
        var details = new[]
        {
            item.IsVehicle ? "Vehicle" : null,
            item.Location,
            item.Category,
            item.Serial is { } serial ? $"serial {serial}" : null,
            item.PurchasedOn is { } bought ? $"bought {bought.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : null,
            item.Price is { } price ? price.ToString("0.00", CultureInfo.CurrentCulture) : null,
            item.WarrantyEnds is { } ends
                ? ends < today ? "warranty ended"
                : ends == today ? "warranty ends today"
                : $"warranty until {ends.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
                : null,
        };
        Details = string.Join(" · ", details.OfType<string>());
        IsWarrantyEnding = item.WarrantyEnds is { } end && end >= today && end <= today.AddDays(InventoryRepository.WarrantyWarningDays);
        WithText = item.BorrowedBy is { } who ? $"With {nameOf(who)}" : null;
        Manual = item.Manual;
        Notes = item.Notes;
        EditCommand = new RelayCommand(() => edit(item));
        DeleteCommand = new RelayCommand(() => delete(item));
        ReturnCommand = new RelayCommand(() => returnIt(item), () => item.BorrowedBy is not null);
        DetailCommand = new RelayCommand(() => showDetail(item));
    }

    /// <summary>The item itself, for its QR label.</summary>
    public InventoryItem Item { get; }

    public string Name { get; }

    public string Details { get; }

    public bool IsWarrantyEnding { get; }

    /// <summary>"With Kai", or null when it's in its place.</summary>
    public string? WithText { get; }

    public bool IsBorrowed => WithText is not null;

    public string? Manual { get; }

    public string? Notes { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    /// <summary>Marks it back in its place.</summary>
    public IRelayCommand ReturnCommand { get; }

    /// <summary>Opens its upkeep and log.</summary>
    public IRelayCommand DetailCommand { get; }
}
