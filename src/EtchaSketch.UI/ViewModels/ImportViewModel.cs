using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Tasks;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>
/// Brings tasks in from a file made by another app (see docs/import.md): choose the file, read
/// what it would do, then confirm. Importing the same file twice adds nothing the second time.
/// </summary>
public partial class ImportViewModel(AppSession session, Household household, Profile me, Action<bool> close, Action? imported = null) : ViewModelBase
{
    private IReadOnlyList<ImportedTask>? _tasks;
    private System.Text.Json.Nodes.JsonObject? _household;
    private bool _imported;

    /// <summary>Bringing a Grocy household in through its API.</summary>
    public GrocyImportViewModel Grocy { get; } = new(session, household, me, () => imported?.Invoke());

    /// <summary>The file's text. The view fills it from a chosen file; it can also be pasted.</summary>
    [ObservableProperty]
    public partial string FileText { get; set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>What would happen (before confirming) or what happened (after).</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial string? Problems { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool CanImport { get; private set; }

    /// <summary>True once the tasks are in. The panel then shows only the result and a Done button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing))]
    public partial bool IsFinished { get; private set; }

    public bool IsChoosing => !IsFinished;

    partial void OnFileTextChanged(string value)
    {
        _tasks = null;
        _household = null;
        _imported = false;
        CanImport = false;
        Summary = null;
        Problems = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void Preview()
    {
        try
        {
            _tasks = TaskImport.Parse(FileText);
            _household = Data.Import.HouseholdBackup.Read(FileText);
            var result = TaskImport.Apply(session.Tasks, household.Id, me.Id, _tasks, dryRun: true);
            var restore = _household is null ? null : session.RestoreHousehold(_household, household.Id, dryRun: true);
            Summary = Describe(result, "Ready to add") + Describe(restore);
            Problems = Joined(result);
            CanImport = result.Added > 0 || restore?.Total > 0;
            ErrorMessage = null;
        }
        catch (FormatException exception)
        {
            _tasks = null;
            _household = null;
            CanImport = false;
            Summary = null;
            Problems = null;
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        if (_tasks is null || _imported)
        {
            return;
        }

        var result = TaskImport.Apply(session.Tasks, household.Id, me.Id, _tasks);
        var restore = _household is null ? null : session.RestoreHousehold(_household, household.Id, dryRun: false);
        _imported = true;
        CanImport = false;
        Summary = Describe(result, "Added") + Describe(restore);
        Problems = Joined(result);
        IsFinished = true;
        imported?.Invoke();
    }

    /// <summary>What turning link lines into links did, or null before it has run.</summary>
    [ObservableProperty]
    public partial string? LinkLinesResult { get; private set; }

    /// <summary>
    /// Turns lines like "Blocked by: Install the SDK", written into notes by an earlier import, into
    /// real links and subtasks.
    /// </summary>
    [RelayCommand]
    private void ConvertLinkLines()
    {
        var result = TaskImport.ConvertLinkLines(session.Tasks, me.Id, session.Now);
        var made = new List<string>();
        if (result.Links > 0)
        {
            made.Add(result.Links == 1 ? "1 link" : $"{result.Links} links");
        }

        if (result.Subtasks > 0)
        {
            made.Add(result.Subtasks == 1 ? "1 step" : $"{result.Subtasks} steps");
        }

        LinkLinesResult = (made.Count == 0 ? "Nothing new to turn into links." : $"Made {string.Join(" and ", made)}.")
            + (result.LinesKept > 0
                ? $" {result.LinesKept} {(result.LinesKept == 1 ? "line names a task" : "lines name tasks")} that can't be found (finished, or two with the same title), so {(result.LinesKept == 1 ? "it stays" : "they stay")} in the notes."
                : "");
        _imported = true;
        imported?.Invoke();
    }

    [RelayCommand]
    private void Close() => close(_imported);

    private static string Describe(ImportResult result, string verb)
    {
        var parts = new List<string> { $"{verb} {Count(result.Added)}" };
        if (result.AlreadyThere > 0)
        {
            parts.Add($"{result.AlreadyThere} already here");
        }

        if (result.Finished > 0)
        {
            parts.Add($"{result.Finished} finished left out");
        }

        if (result.Problems.Count > 0)
        {
            parts.Add($"{result.Problems.Count} can't be imported");
        }

        return string.Join(", ", parts) + ".";
    }

    private static string Count(int number) => number == 1 ? "1 task" : $"{number} tasks";

    private static string Singular(string plural) => plural switch
    {
        "things" => "thing",
        "log entries" => "log entry",
        _ => plural.TrimEnd('s'),
    };

    // " Household: 12 pantry items, 3 recipes (40 already here)." for an export's shared modules.
    private static string Describe(Data.Import.HouseholdRestore? restore) =>
        restore is null || restore.Total == 0 && restore.AlreadyThere == 0
            ? ""
            : " Household: " + (restore.Total == 0 ? "nothing new" : string.Join(", ", restore.Added.Select(pair => $"{pair.Value} {(pair.Value == 1 ? Singular(pair.Key) : pair.Key)}")))
              + (restore.AlreadyThere > 0 ? $" ({restore.AlreadyThere} already here)" : "") + ".";

    private static string? Joined(ImportResult result) =>
        result.Problems.Count == 0 ? null : string.Join('\n', result.Problems.Take(10)) + (result.Problems.Count > 10 ? $"\n…and {result.Problems.Count - 10} more." : "");
}
