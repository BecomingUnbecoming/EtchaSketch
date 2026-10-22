using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Import;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

public enum ThingsSource
{
    Homebox,
    LubeLogger,
}

public sealed record ThingsSourceOption(string Label, ThingsSource Source)
{
    public override string ToString() => Label;
}

public sealed record VehicleOption(string Label, InventoryItem Vehicle)
{
    public override string ToString() => Label;
}

/// <summary>
/// Brings things in from Homebox, or a vehicle's records from LubeLogger, from the CSV files those
/// apps export: choose the file, read what it would do, then confirm.
/// </summary>
public partial class ThingsImportViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Household _household;
    private readonly Action _imported;
    private readonly Action _close;

    public ThingsImportViewModel(AppSession session, Household household, Action imported, Action close)
    {
        _session = session;
        _household = household;
        _imported = imported;
        _close = close;
        Vehicles = [.. session.Inventory.Items(household.Id).Where(item => item.IsVehicle).Select(item => new VehicleOption(item.Name, item))];
        Vehicle = Vehicles.FirstOrDefault();
        Source = Sources[0];
        Kind = Kinds[0];
    }

    public IReadOnlyList<ThingsSourceOption> Sources { get; } =
    [
        new("Homebox (Tools → Export inventory)", ThingsSource.Homebox),
        new("LubeLogger (a vehicle's records, exported as CSV)", ThingsSource.LubeLogger),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLubeLogger))]
    public partial ThingsSourceOption Source { get; set; }

    public bool IsLubeLogger => Source.Source == ThingsSource.LubeLogger;

    public IReadOnlyList<VehicleOption> Vehicles { get; }

    public bool HasNoVehicles => Vehicles.Count == 0;

    [ObservableProperty]
    public partial VehicleOption? Vehicle { get; set; }

    /// <summary>What the LubeLogger file holds. A fuel file is recognised by itself.</summary>
    public IReadOnlyList<LogKindOption> Kinds { get; } =
    [
        new("Fuel records", LogKind.Fuel), new("Service records", LogKind.Service), new("Repairs", LogKind.Repair), new("Upgrades or other notes", LogKind.Note),
    ];

    [ObservableProperty]
    public partial LogKindOption Kind { get; set; }

    /// <summary>The file's text. The view fills it from a chosen file.</summary>
    [ObservableProperty]
    public partial string FileText { get; set; } = "";

    [ObservableProperty]
    public partial string? FileName { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial string? Problems { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool CanImport { get; private set; }

    partial void OnFileTextChanged(string value) => Reset();

    partial void OnSourceChanged(ThingsSourceOption value) => Reset();

    partial void OnVehicleChanged(VehicleOption? value) => Reset();

    partial void OnKindChanged(LogKindOption value) => Reset();

    [RelayCommand]
    private void Preview() => Run(dryRun: true);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        Run(dryRun: false);
        CanImport = false;
        _imported();
    }

    [RelayCommand]
    private void Close() => _close();

    private void Run(bool dryRun)
    {
        if (FileText.Length == 0)
        {
            ErrorMessage = "Choose a file first.";
            return;
        }

        try
        {
            ThingsImportResult result;
            if (Source.Source == ThingsSource.Homebox)
            {
                result = HomeboxImport.Apply(_session.Inventory, _household.Id, FileText, dryRun);
            }
            else if (Vehicle is { } vehicle)
            {
                result = LubeLoggerImport.Apply(_session.Upkeep, vehicle.Vehicle, Kind.Kind, FileText, dryRun);
            }
            else
            {
                ErrorMessage = "Add the vehicle on the Things tab first (tick \"It's a vehicle\"), then import its records into it.";
                return;
            }

            var what = Source.Source == ThingsSource.Homebox ? "thing" : "log entry";
            var plural = Source.Source == ThingsSource.Homebox ? "things" : "log entries";
            var parts = new List<string> { $"{(dryRun ? "Ready to add" : "Added")} {result.Added} {(result.Added == 1 ? what : plural)}" };
            if (result.AlreadyThere > 0)
            {
                parts.Add($"{result.AlreadyThere} already here");
            }

            if (result.Skipped.Count > 0)
            {
                parts.Add($"{result.Skipped.Count} left out");
            }

            Summary = string.Join(", ", parts) + "." + (result.Note is { } note ? " " + note : "");
            Problems = result.Skipped.Count == 0 ? null : string.Join("\n", result.Skipped);
            ErrorMessage = null;
            CanImport = dryRun && result.Added > 0;
        }
        catch (FormatException exception)
        {
            ErrorMessage = exception.Message;
            Summary = null;
            Problems = null;
            CanImport = false;
        }
    }

    private void Reset()
    {
        Summary = null;
        Problems = null;
        ErrorMessage = null;
        CanImport = false;
    }
}
