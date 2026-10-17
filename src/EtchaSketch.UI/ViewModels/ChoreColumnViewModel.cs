using System.Collections.Generic;

namespace EtchaSketch.UI.ViewModels;

/// <summary>One person's column on the chore board, or the "Anyone" column for unassigned chores.</summary>
public sealed record ChoreColumnViewModel(string Name, IReadOnlyList<TaskRowViewModel> Chores);
