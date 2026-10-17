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
public partial class ImportViewModel(AppSession session, Household household, Profile me, Action<bool> close) : ViewModelBase
{
    private IReadOnlyList<ImportedTask>? _tasks;
    private bool _imported;

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

    partial void OnFileTextChanged(string value)
    {
        _tasks = null;
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
            var result = TaskImport.Apply(session.Tasks, household.Id, me.Id, _tasks, dryRun: true);
            Summary = Describe(result, "Ready to add");
            Problems = Joined(result);
            CanImport = result.Added > 0;
            ErrorMessage = null;
        }
        catch (FormatException exception)
        {
            _tasks = null;
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
        _imported = true;
        CanImport = false;
        Summary = Describe(result, "Added");
        Problems = Joined(result);
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

    private static string? Joined(ImportResult result) =>
        result.Problems.Count == 0 ? null : string.Join('\n', result.Problems.Take(10)) + (result.Problems.Count > 10 ? $"\n…and {result.Problems.Count - 10} more." : "");
}
