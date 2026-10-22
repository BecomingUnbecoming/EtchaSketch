using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Tasks;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI.ViewModels;

/// <summary>What an export can be saved as.</summary>
public enum ExportKind
{
    /// <summary>Everything, in a file EtchaSketch can import again.</summary>
    Everything,
    Spreadsheet,
    Calendar,
}

/// <summary>
/// Saves a copy of what this person can see: everything (to bring back into EtchaSketch or keep as a
/// backup), a spreadsheet, or a calendar file. Other people's private tasks are never included.
/// </summary>
public partial class ExportViewModel(AppSession session, Household household, Profile me, Action close) : ViewModelBase
{
    /// <summary>Says what was saved, or why it couldn't be.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>The file name to suggest, like etchasketch-robin-2026-10-21.json.</summary>
    public string FileName(ExportKind kind)
    {
        var name = string.Concat(me.DisplayName.ToLowerInvariant().Split(System.IO.Path.GetInvalidFileNameChars())).Replace(' ', '-');
        var day = session.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return $"etchasketch-{name}-{day}.{Extension(kind)}";
    }

    public static string Extension(ExportKind kind) => kind switch
    {
        ExportKind.Spreadsheet => "csv",
        ExportKind.Calendar => "ics",
        _ => "json",
    };

    /// <summary>The file's contents.</summary>
    public string Build(ExportKind kind) => kind switch
    {
        ExportKind.Spreadsheet => TaskExport.ToCsv(session.Tasks, household, me),
        ExportKind.Calendar => TaskExport.ToIcs(session.Tasks, me, session.Now),
        _ => TaskExport.ToJson(session.Tasks, me, session.Now),
    };

    public void Saved(ExportKind kind, string where) => Status = kind switch
    {
        ExportKind.Everything => $"Saved to {where}. Import it to bring everything back. It's private: keep it somewhere safe.",
        ExportKind.Calendar => $"Saved to {where}. Open it with a calendar app to add your dated tasks.",
        _ => $"Saved to {where}.",
    };

    public void Failed(string reason) => Status = $"Couldn't save: {reason}";

    [RelayCommand]
    private void Close() => close();
}
