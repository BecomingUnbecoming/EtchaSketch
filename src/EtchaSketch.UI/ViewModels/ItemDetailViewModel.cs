using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Pantry;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>One thing's upkeep (what it needs every so often) and its log (fuel, services, repairs, charges).</summary>
public partial class ItemDetailViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Action _changed;
    private readonly Action _close;
    private InventoryItem _item;

    public ItemDetailViewModel(AppSession session, InventoryItem item, Action changed, Action close)
    {
        _session = session;
        _item = item;
        _changed = changed;
        _close = close;
        EntryKind = LogKinds[0];
        Refresh();
    }

    public Guid ItemId => _item.Id;

    public string Name => _item.Name;

    public bool IsVehicle => _item.IsVehicle;

    /// <summary>What fuel is measured in, to match the distance unit.</summary>
    public string AmountLabel => _item.DistanceUnit == "mi" ? "Gallons" : "Litres";

    /// <summary>"km" or "mi", for the labels.</summary>
    public string DistanceUnit => _item.DistanceUnit ?? "";

    /// <summary>"At 40,100 km · spent 359.25 in total", or null when there's nothing to say yet.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    public ObservableCollection<UpkeepRowViewModel> Upkeep { get; } = [];

    public ObservableCollection<LogRowViewModel> Log { get; } = [];

    public bool HasNoUpkeep => Upkeep.Count == 0;

    public bool HasNoLog => Log.Count == 0;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    // Adding upkeep.

    [ObservableProperty]
    public partial string? UpkeepName { get; set; }

    [ObservableProperty]
    public partial decimal? EveryDays { get; set; }

    [ObservableProperty]
    public partial decimal? EveryDistance { get; set; }

    [ObservableProperty]
    public partial DateTime? LastDoneOn { get; set; }

    [ObservableProperty]
    public partial decimal? LastDoneDistance { get; set; }

    // Adding a log entry.

    public IReadOnlyList<LogKindOption> LogKinds { get; } =
    [
        new("Fuel", LogKind.Fuel), new("Service", LogKind.Service), new("Repair", LogKind.Repair), new("Charge", LogKind.Charge), new("Note", LogKind.Note),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFuel))]
    public partial LogKindOption EntryKind { get; set; }

    public bool IsFuel => EntryKind.Kind == LogKind.Fuel;

    [ObservableProperty]
    public partial DateTime? EntryOn { get; set; }

    [ObservableProperty]
    public partial decimal? EntryDistance { get; set; }

    [ObservableProperty]
    public partial decimal? EntryAmount { get; set; }

    [ObservableProperty]
    public partial decimal? EntryCost { get; set; }

    [ObservableProperty]
    public partial string? EntryNote { get; set; }

    /// <summary>Which upkeep this entry did, if any: "Nothing on the list" first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<UpkeepOption> UpkeepOptions { get; private set; } = [];

    [ObservableProperty]
    public partial UpkeepOption? EntryUpkeep { get; set; }

    public void Refresh()
    {
        _item = _session.Inventory.Get(_item.Id) ?? _item;
        var today = _session.Today;
        var distanceNow = _session.Upkeep.DistanceNow(_item.Id);

        Upkeep.Clear();
        foreach (var upkeep in _session.Upkeep.UpkeepFor(_item.Id))
        {
            Upkeep.Add(new UpkeepRowViewModel(upkeep, _item, today, distanceNow, _session.Upkeep.TimesDone(upkeep.Id), DoneToday, DeleteUpkeep));
        }

        var upkeepNames = Upkeep.ToDictionary(row => row.Id, row => row.Name);
        Log.Clear();
        foreach (var entry in _session.Upkeep.LogFor(_item.Id))
        {
            Log.Add(new LogRowViewModel(entry, _item, entry.UpkeepId is { } id ? upkeepNames.GetValueOrDefault(id) : null, DeleteEntry));
        }

        var cost = _session.Upkeep.TotalCost(_item.Id);
        var parts = new[]
        {
            distanceNow is { } at ? $"At {at.ToString("N0", CultureInfo.CurrentCulture)} {_item.DistanceUnit}" : null,
            cost > 0 ? $"spent {cost.ToString("0.00", CultureInfo.CurrentCulture)} in total" : null,
        }.OfType<string>().ToList();
        Summary = parts.Count == 0 ? null : string.Join(" · ", parts);
        if (Summary is { Length: > 0 } text)
        {
            Summary = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        }

        UpkeepOptions = [new UpkeepOption("Nothing on the list", null), .. Upkeep.Select(row => new UpkeepOption(row.Name, row.Id))];
        EntryUpkeep = UpkeepOptions[0];
        LastDoneOn ??= today.ToDateTime(TimeOnly.MinValue);
        EntryOn ??= today.ToDateTime(TimeOnly.MinValue);
        OnPropertyChanged(nameof(HasNoUpkeep));
        OnPropertyChanged(nameof(HasNoLog));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsVehicle));
        OnPropertyChanged(nameof(DistanceUnit));
        OnPropertyChanged(nameof(AmountLabel));
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private void AddUpkeep()
    {
        try
        {
            _session.Upkeep.AddUpkeep(new Upkeep
            {
                HouseholdId = _item.HouseholdId,
                ItemId = _item.Id,
                Name = UpkeepName ?? "",
                EveryDays = Whole(EveryDays),
                EveryDistance = IsVehicle ? Whole(EveryDistance) : null,
                LastDoneOn = DateOnly.FromDateTime(LastDoneOn ?? _session.Today.ToDateTime(TimeOnly.MinValue)),
                LastDoneDistance = IsVehicle ? Whole(LastDoneDistance) ?? _session.Upkeep.DistanceNow(_item.Id) : null,
            });
            UpkeepName = null;
            EveryDays = null;
            EveryDistance = null;
            LastDoneOn = null;
            LastDoneDistance = null;
            Message = null;
            Updated();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    [RelayCommand]
    private void AddEntry()
    {
        try
        {
            _session.Upkeep.Log(new ItemLogEntry
            {
                HouseholdId = _item.HouseholdId,
                ItemId = _item.Id,
                Kind = EntryKind.Kind,
                On = DateOnly.FromDateTime(EntryOn ?? _session.Today.ToDateTime(TimeOnly.MinValue)),
                Distance = IsVehicle ? Whole(EntryDistance) : null,
                Amount = IsFuel ? EntryAmount : null,
                Cost = EntryCost,
                Note = EntryNote,
                UpkeepId = EntryUpkeep?.Id,
            });
            EntryDistance = null;
            EntryAmount = null;
            EntryCost = null;
            EntryNote = null;
            EntryOn = null;
            Message = null;
            Updated();
        }
        catch (ArgumentException exception)
        {
            Message = Messages.Friendly(exception);
        }
    }

    private void DoneToday(Upkeep upkeep)
    {
        _session.Upkeep.Log(new ItemLogEntry
        {
            HouseholdId = _item.HouseholdId,
            ItemId = _item.Id,
            Kind = LogKind.Service,
            On = _session.Today,
            Distance = _item.IsVehicle ? _session.Upkeep.DistanceNow(_item.Id) : null,
            UpkeepId = upkeep.Id,
        });
        Message = $"{upkeep.Name} is done.";
        Updated();
    }

    private void DeleteUpkeep(Upkeep upkeep)
    {
        _session.Upkeep.DeleteUpkeep(upkeep.Id);
        Updated();
    }

    private void DeleteEntry(ItemLogEntry entry)
    {
        _session.Upkeep.DeleteLog(entry.Id);
        Updated();
    }

    private void Updated()
    {
        Refresh();
        _changed();
    }

    private static int? Whole(decimal? value) => value is { } number ? (int)Math.Round(number) : null;
}

public sealed record LogKindOption(string Label, LogKind Kind)
{
    public override string ToString() => Label;
}

public sealed record UpkeepOption(string Label, Guid? Id)
{
    public override string ToString() => Label;
}

/// <summary>"Oil change · every 10,000 km or 365 days · next at 50,000 km or 21 Oct 2027".</summary>
public sealed class UpkeepRowViewModel
{
    public UpkeepRowViewModel(Upkeep upkeep, InventoryItem item, DateOnly today, int? distanceNow, int timesDone,
        Action<Upkeep> doneToday, Action<Upkeep> delete)
    {
        Id = upkeep.Id;
        Name = upkeep.Name;
        var unit = item.DistanceUnit;
        var every = new[]
        {
            upkeep.EveryDistance is { } distance ? $"{distance.ToString("N0", CultureInfo.CurrentCulture)} {unit}" : null,
            upkeep.EveryDays is { } days ? $"{days} days" : null,
        }.OfType<string>();
        var next = new[]
        {
            upkeep.NextAtDistance is { } at && upkeep.EveryDistance is not null ? $"{at.ToString("N0", CultureInfo.CurrentCulture)} {unit}" : null,
            upkeep.NextOn is { } on ? on.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null,
        }.OfType<string>();
        IsDue = upkeep.IsDue(today, distanceNow);
        Details = string.Join(" · ", new[]
        {
            "every " + string.Join(" or ", every),
            IsDue ? "due now" : "next " + string.Join(" or ", next),
            timesDone > 0 ? timesDone == 1 ? "done once" : $"done {timesDone} times" : null,
        }.OfType<string>());
        DoneTodayCommand = new RelayCommand(() => doneToday(upkeep));
        DeleteCommand = new RelayCommand(() => delete(upkeep));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Details { get; }

    public bool IsDue { get; }

    public IRelayCommand DoneTodayCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}

/// <summary>"21 Oct 2026 · Fuel · 40,100 km · 40 l · 70.00 · full tank".</summary>
public sealed class LogRowViewModel
{
    public LogRowViewModel(ItemLogEntry entry, InventoryItem item, string? upkeepName, Action<ItemLogEntry> delete)
    {
        Text = string.Join(" · ", new[]
        {
            entry.On.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
            upkeepName ?? entry.Kind.ToString(),
            entry.Distance is { } distance ? $"{distance.ToString("N0", CultureInfo.CurrentCulture)} {item.DistanceUnit}" : null,
            entry.Amount is { } amount ? $"{amount.ToString("0.##", CultureInfo.CurrentCulture)} {(item.DistanceUnit == "mi" ? "gal" : "l")}" : null,
            entry.Cost is { } cost ? cost.ToString("0.00", CultureInfo.CurrentCulture) : null,
            entry.Note,
        }.OfType<string>());
        DeleteCommand = new RelayCommand(() => delete(entry));
    }

    public string Text { get; }

    public IRelayCommand DeleteCommand { get; }
}
