using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Import;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// Brings a Grocy household in through Grocy's API: chores, stock, batteries and recipes. Reads
/// only; the API key is kept just while this panel is open.
/// </summary>
public partial class GrocyImportViewModel(AppSession session, Household household, Profile me, Action imported) : ViewModelBase
{
    private GrocySnapshot? _snapshot;

    /// <summary>Grocy's address, like https://grocy.example.com.</summary>
    [ObservableProperty]
    public partial string? Address { get; set; }

    /// <summary>From Grocy: user menu → Manage API keys → Add.</summary>
    [ObservableProperty]
    public partial string? ApiKey { get; set; }

    /// <summary>
    /// Off: chores keep their repeat but no due date, so nothing reminds until each is first done.
    /// On: chores are due when Grocy says, and reminders start.
    /// </summary>
    [ObservableProperty]
    public partial bool KeepDueDates { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ImportCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool CanImport { get; private set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial string? Problems { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    partial void OnAddressChanged(string? value) => Forget();

    partial void OnApiKeyChanged(string? value) => Forget();

    partial void OnKeepDueDatesChanged(bool value)
    {
        if (_snapshot is not null)
        {
            Show(dryRun: true);
        }
    }

    private bool CanCheck() => !IsBusy;

    /// <summary>Reads Grocy and says what an import would bring in.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check()
    {
        if (!Uri.TryCreate(Address?.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http"))
        {
            ErrorMessage = "Type Grocy's address, like https://grocy.example.com.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ErrorMessage = "Paste an API key from Grocy (user menu → Manage API keys).";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var root = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
            _snapshot = await GrocyImport.LoadAsync(session.GrocyReader(root, ApiKey.Trim()));
            Show(dryRun: true);
        }
        catch (FormatException exception)
        {
            Fail(exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            Fail($"Couldn't read Grocy at that address: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportNow))]
    private void Import()
    {
        if (_snapshot is null)
        {
            return;
        }

        Show(dryRun: false);
        CanImport = false;
        imported();
    }

    private bool CanImportNow() => CanImport && !IsBusy;

    private void Show(bool dryRun)
    {
        var result = GrocyImport.Apply(_snapshot!, session.Tasks, session.Pantry, session.Inventory, session.Upkeep, session.Meals,
            household.Id, me.Id, KeepDueDates, session.Today, dryRun);
        var parts = new List<string>
        {
            Count(result.Chores, "chore"), Count(result.Stock, "pantry item"), Count(result.Batteries, "battery", "batteries"), Count(result.Recipes, "recipe"),
        };
        Summary = $"{(dryRun ? "Ready to add" : "Added")} {string.Join(", ", parts)}."
            + (result.AlreadyThere > 0 ? $" {result.AlreadyThere} already here." : "")
            + (result.Chores > 0 ? KeepDueDates ? " Chores are due when Grocy says, so reminders start." : " Chores have no due date yet: each is first due a repeat after it's next done." : "");
        Problems = result.Skipped.Count == 0 ? null : string.Join("\n", result.Skipped);
        CanImport = dryRun && result.Chores + result.Stock + result.Batteries + result.Recipes > 0;
    }

    private static string Count(int count, string one, string? many = null) => $"{count} {(count == 1 ? one : many ?? one + "s")}";

    private void Fail(string message)
    {
        _snapshot = null;
        ErrorMessage = message;
        Summary = null;
        Problems = null;
        CanImport = false;
    }

    private void Forget()
    {
        _snapshot = null;
        Summary = null;
        Problems = null;
        CanImport = false;
    }
}
