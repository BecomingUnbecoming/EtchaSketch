using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Tasks;

namespace EtchaSketch.Data.Tasks;

/// <summary>One task as it is written in an import file.</summary>
public sealed record ImportedTask
{
    public string Title { get; init; } = "";

    public string? Notes { get; init; }

    /// <summary>A date, like 2026-10-21.</summary>
    public string? Due { get; init; }

    /// <summary>low, normal, high or urgent. Anything else counts as normal.</summary>
    public string? Priority { get; init; }

    public ImportedRepeat? Repeat { get; init; }

    /// <summary>Whether the whole household sees it. Otherwise it is private to the person importing.</summary>
    public bool Shared { get; init; }

    /// <summary>Finished tasks are left out.</summary>
    public bool Done { get; init; }

    /// <summary>Where it came from, like vikunja:123. Importing the same file twice adds nothing the second time.</summary>
    public string? Source { get; init; }

    public string? Project { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    /// <summary>Remind this many days before the due date.</summary>
    public int? RemindDaysBefore { get; init; }

    /// <summary>A time of day on the due date, like 14:05.</summary>
    public string? DueTime { get; init; }

    /// <summary>Also remind this many minutes before the due time.</summary>
    public int? RemindMinutesBefore { get; init; }

    /// <summary>Remind like an alarm. Needs a due time.</summary>
    public bool? Alarm { get; init; }

    /// <summary>The <see cref="Source"/> of the task this one is a step of.</summary>
    public string? Parent { get; init; }

    /// <summary>Links to other tasks, each named by its <see cref="Source"/>.</summary>
    public IReadOnlyList<ImportedLink>? Links { get; init; }
}

public sealed record ImportedRepeat(int Every, string Unit);

/// <summary>A link to another task in the file (or imported before): "blocks", "blockedBy" or "related".</summary>
public sealed record ImportedLink(string Kind, string Source);

/// <summary>What an import did (or, for a preview, would do).</summary>
public sealed record ImportResult(int Added, int AlreadyThere, int Finished, IReadOnlyList<string> Problems);

/// <summary>
/// Brings tasks in from a file, so they can be moved from another app (see docs/import.md). A
/// file is JSON: <c>{"format":"etchasketch-tasks","version":1,"tasks":[{"title":"…"}]}</c>.
/// </summary>
public static class TaskImport
{
    public const string Format = "etchasketch-tasks";
    public const int MaxTasks = 5_000;
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 20_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Document(string? Format, int Version, List<ImportedTask>? Tasks);

    /// <exception cref="FormatException">The text isn't an EtchaSketch task file.</exception>
    public static IReadOnlyList<ImportedTask> Parse(string text)
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(text, Json);
        }
        catch (JsonException)
        {
            throw new FormatException("That isn't a task file. It should be JSON; see docs/import.md.");
        }

        if (document is not { Format: Format, Version: 1, Tasks: { } tasks })
        {
            throw new FormatException($"That isn't an EtchaSketch task file. It should say \"format\": \"{Format}\" and \"version\": 1.");
        }

        return tasks.Count > MaxTasks
            ? throw new FormatException($"That file has {tasks.Count} tasks; import at most {MaxTasks} at a time.")
            : tasks;
    }

    /// <summary>
    /// Adds the tasks for one person. With <paramref name="dryRun"/> nothing is saved, so a preview
    /// can say what would happen. A task already imported from the same source is skipped.
    /// </summary>
    public static ImportResult Apply(TaskRepository tasks, Guid householdId, Guid ownerId, IEnumerable<ImportedTask> imported, bool dryRun = false)
    {
        var known = tasks.VisibleTo(ownerId)
            .Where(task => task.OwnerId == ownerId)
            .Select(task => task.Notes)
            .OfType<string>()
            .SelectMany(notes => notes.Split('\n'))
            .Where(line => line.StartsWith(SourcePrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        int added = 0, already = 0, finished = 0;
        var seenIds = new HashSet<Guid>();
        var bySource = SourcesOf(tasks, ownerId);
        var problems = new List<string>();
        foreach (var (item, number) in imported.Select((item, index) => (item, index + 1)))
        {
            if (item.Done)
            {
                finished++;
                continue;
            }

            // A task exported from EtchaSketch comes back as itself (same id, notes unchanged), so
            // importing your own export restores it rather than adding a copy.
            Guid? ownId = item.Source?.Trim() is { } source && source.StartsWith(TaskExport.SourcePrefix, StringComparison.Ordinal)
                && Guid.TryParse(source[TaskExport.SourcePrefix.Length..], out var parsed) ? parsed : null;
            if (ownId is { } existing && (tasks.Get(existing) is not null || !seenIds.Add(existing)))
            {
                already++;
                continue;
            }

            var sourceLine = ownId is not null || string.IsNullOrWhiteSpace(item.Source) ? null : SourcePrefix + item.Source.Trim();
            if (sourceLine is not null && known.Contains(sourceLine))
            {
                already++;
                continue;
            }

            try
            {
                var task = ToTask(item, sourceLine, householdId, ownerId);
                if (ownId is { } id)
                {
                    task = task with { Id = id };
                }

                if (!dryRun)
                {
                    tasks.Add(task);
                    if (!string.IsNullOrWhiteSpace(item.Source))
                    {
                        bySource[item.Source.Trim()] = task.Id;
                    }
                }

                if (sourceLine is not null)
                {
                    known.Add(sourceLine);
                }

                added++;
            }
            catch (ArgumentException exception)
            {
                problems.Add($"Task {number} (\"{Shorten(item.Title)}\"): {exception.Message.Split(" (Parameter")[0]}");
            }
        }

        if (!dryRun)
        {
            ConnectAll(tasks, imported, bySource, problems);
        }

        return new ImportResult(added, already, finished, problems);
    }

    // Every source this person's tasks were imported from, and each task's own export source, to the task.
    private static Dictionary<string, Guid> SourcesOf(TaskRepository tasks, Guid ownerId)
    {
        var sources = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var task in tasks.ExportableBy(ownerId))
        {
            sources[TaskExport.SourcePrefix + task.Id] = task.Id;
            if (task.OwnerId == ownerId && task.Notes is { } notes)
            {
                foreach (var line in notes.Split('\n').Where(line => line.StartsWith(SourcePrefix, StringComparison.Ordinal)))
                {
                    sources[line[SourcePrefix.Length..].Trim()] = task.Id;
                }
            }
        }

        return sources;
    }

    // Subtasks and links point at other tasks by source, so they're made once every task is in.
    private static void ConnectAll(TaskRepository tasks, IEnumerable<ImportedTask> imported, Dictionary<string, Guid> bySource, List<string> problems)
    {
        foreach (var (item, number) in imported.Select((item, index) => (item, index + 1)))
        {
            if (item.Done || item.Source?.Trim() is not { Length: > 0 } source || !bySource.TryGetValue(source, out var id)
                || (item.Parent is null && item.Links is not { Count: > 0 }))
            {
                continue;
            }

            try
            {
                if (item.Parent?.Trim() is { Length: > 0 } parent)
                {
                    if (!bySource.TryGetValue(parent, out var parentId))
                    {
                        throw new ArgumentException($"Its parent task ({parent}) isn't among your open tasks (it may be finished), so it stays a task of its own.");
                    }

                    if (tasks.Get(id) is { } task && task.ParentId != parentId)
                    {
                        tasks.Update(task with { ParentId = parentId });
                    }
                }

                foreach (var link in item.Links ?? [])
                {
                    if (!bySource.TryGetValue(link.Source?.Trim() ?? "", out var otherId))
                    {
                        throw new ArgumentException($"A linked task ({link.Source}) isn't among your open tasks (it may be finished).");
                    }

                    var at = DateTimeOffset.UtcNow;
                    switch ((link.Kind ?? "").Trim().ToLowerInvariant())
                    {
                        case "blocks":
                            tasks.AddLink(new TaskLink(id, otherId, TaskLinkKind.Blocks), at);
                            break;
                        case "blockedby":
                            tasks.AddLink(new TaskLink(otherId, id, TaskLinkKind.Blocks), at);
                            break;
                        case "related":
                            tasks.AddLink(new TaskLink(id, otherId, TaskLinkKind.Related), at);
                            break;
                        default:
                            throw new ArgumentException($"A link's kind must be blocks, blockedBy or related, not \"{link.Kind}\".");
                    }
                }
            }
            catch (ArgumentException exception)
            {
                problems.Add($"Task {number} (\"{Shorten(item.Title)}\"): {exception.Message.Split(" (Parameter")[0]}");
            }
        }
    }

    /// <summary>What turning link lines into real links did.</summary>
    public sealed record LinkConversion(int Links, int Subtasks, int LinesKept);

    // The lines an earlier import (or the Vikunja converter) wrote into notes for links it couldn't make.
    private static readonly (string Label, Func<Guid, Guid, TaskLink?> Link)[] LinkLabels =
    [
        ("Blocked by", (self, other) => new TaskLink(other, self, TaskLinkKind.Blocks)),
        ("Blocking", (self, other) => new TaskLink(self, other, TaskLinkKind.Blocks)),
        ("Comes before", (self, other) => new TaskLink(self, other, TaskLinkKind.Blocks)),
        ("Follows", (self, other) => new TaskLink(other, self, TaskLinkKind.Blocks)),
        ("Related", (self, other) => new TaskLink(self, other, TaskLinkKind.Related)),
        ("Duplicates", (self, other) => new TaskLink(self, other, TaskLinkKind.Related)),
        ("Duplicate of", (self, other) => new TaskLink(self, other, TaskLinkKind.Related)),
    ];

    /// <summary>
    /// Turns lines like "Blocked by: Install the SDK" in this person's notes into real links, and
    /// "Parent task: Move house" into a subtask, then removes those lines. A line naming a task that
    /// can't be found (finished and left out, or two tasks with that title) stays as it is. Safe to
    /// run more than once.
    /// </summary>
    /// <remarks>
    /// Vikunja writes each link on both tasks, so both lines are understood and the link is made once.
    /// Its "Subtask of" line lists the task's own subtasks; it is only removed once each one has this
    /// task as its parent (from the matching "Parent task" line), and never used to choose a parent.
    /// </remarks>
    public static LinkConversion ConvertLinkLines(TaskRepository tasks, Guid ownerId, DateTimeOffset at)
    {
        var all = tasks.ExportableBy(ownerId);
        var byTitle = all.GroupBy(task => task.Title, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Id, StringComparer.Ordinal);

        int links = 0, subtasks = 0, kept = 0;
        var owned = all.Where(task => task.OwnerId == ownerId && task.Notes is not null).ToList();

        // Parents first, so "Subtask of" lines can be checked against them afterwards.
        foreach (var task in owned)
        {
            foreach (var line in task.Notes!.Split('\n'))
            {
                if (Labelled(line, "Parent task") is { } names && Resolve(names, byTitle) is [var parentId]
                    && tasks.Get(task.Id) is { ParentId: null } current)
                {
                    try
                    {
                        tasks.Update(current with { ParentId = parentId });
                        subtasks++;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }

        foreach (var task in owned)
        {
            var current = tasks.Get(task.Id)!;
            var remaining = new List<string>();
            foreach (var line in current.Notes!.Split('\n'))
            {
                if (!Convert(tasks, current, line, byTitle, at, ref links))
                {
                    remaining.Add(line);
                    if (LinkLabels.Any(label => Labelled(line, label.Label) is not null)
                        || Labelled(line, "Parent task") is not null || Labelled(line, "Subtask of") is not null)
                    {
                        kept++;
                    }
                }
            }

            var notes = string.Join('\n', remaining).Trim();
            while (notes.Contains("\n\n\n", StringComparison.Ordinal))
            {
                notes = notes.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
            }

            if (notes != current.Notes)
            {
                tasks.Update(current with { Notes = notes.Length == 0 ? null : notes });
            }
        }

        return new LinkConversion(links, subtasks, kept);
    }

    // Makes what one line describes. True when the line has been dealt with and can go.
    private static bool Convert(TaskRepository tasks, TaskItem task, string line, Dictionary<string, Guid> byTitle, DateTimeOffset at, ref int links)
    {
        if (Labelled(line, "Parent task") is { } parentNames)
        {
            return Resolve(parentNames, byTitle) is [var parentId] && tasks.Get(task.Id)?.ParentId == parentId;
        }

        if (Labelled(line, "Subtask of") is { } childNames)
        {
            return Resolve(childNames, byTitle) is { } children && children.All(child => tasks.Get(child)?.ParentId == task.Id);
        }

        foreach (var (label, make) in LinkLabels)
        {
            if (Labelled(line, label) is { } names)
            {
                if (Resolve(names, byTitle) is not { } others)
                {
                    return false;
                }

                foreach (var other in others)
                {
                    try
                    {
                        if (make(task.Id, other) is { } link && tasks.AddLink(link, at))
                        {
                            links++;
                        }
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static string? Labelled(string line, string label) =>
        line.StartsWith(label + ": ", StringComparison.Ordinal) ? line[(label.Length + 2)..].Trim() : null;

    // Titles were joined with ", ", and a title may hold a comma itself, so the longest titles that match are taken first.
    private static List<Guid>? Resolve(string names, Dictionary<string, Guid> byTitle)
    {
        var pieces = names.Split(", ");
        var found = new List<Guid>();
        var i = 0;
        while (i < pieces.Length)
        {
            var match = Enumerable.Range(i + 1, pieces.Length - i).Reverse()
                .Select(end => (End: end, Title: string.Join(", ", pieces[i..end])))
                .FirstOrDefault(candidate => byTitle.ContainsKey(candidate.Title));
            if (match.Title is null)
            {
                return null;
            }

            found.Add(byTitle[match.Title]);
            i = match.End;
        }

        return found.Count == 0 ? null : found;
    }

    private const string SourcePrefix = "Imported from ";

    private static TaskItem ToTask(ImportedTask item, string? sourceLine, Guid householdId, Guid ownerId)
    {
        var title = (item.Title ?? "").Trim();
        if (title.Length is 0 or > MaxTitleLength)
        {
            throw new ArgumentException(title.Length == 0 ? "It has no title." : $"The title is longer than {MaxTitleLength} characters.");
        }

        DateOnly? due = null;
        if (!string.IsNullOrWhiteSpace(item.Due))
        {
            due = DateOnly.TryParseExact(item.Due.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : throw new ArgumentException($"\"{item.Due}\" isn't a date like 2026-10-21.");
        }

        TimeOnly? dueTime = null;
        if (!string.IsNullOrWhiteSpace(item.DueTime))
        {
            dueTime = TimeOnly.TryParseExact(item.DueTime.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : throw new ArgumentException($"\"{item.DueTime}\" isn't a time like 14:05.");
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            lines.Add(item.Notes.Trim());
        }

        if (sourceLine is not null)
        {
            lines.Add(sourceLine);
        }

        var notes = lines.Count == 0 ? null : string.Join("\n\n", lines);
        if (notes is { Length: > MaxNotesLength })
        {
            throw new ArgumentException($"The notes are longer than {MaxNotesLength} characters.");
        }

        return new TaskItem
        {
            HouseholdId = householdId,
            OwnerId = ownerId,
            Title = title,
            Notes = notes,
            Due = due,
            Priority = (item.Priority ?? "").Trim().ToLowerInvariant() switch
            {
                "low" => TaskPriority.Low,
                "high" => TaskPriority.High,
                "urgent" => TaskPriority.Urgent,
                _ => TaskPriority.Normal,
            },
            Recurrence = Repeat(item.Repeat),
            Project = item.Project,
            Labels = item.Labels?.Where(label => !string.IsNullOrWhiteSpace(label)).ToList() ?? [],
            RemindDaysBefore = item.RemindDaysBefore,
            DueTime = dueTime,
            RemindMinutesBefore = item.RemindMinutesBefore,
            Alarm = item.Alarm == true,
            Tier = item.Shared ? PrivacyTier.Household : PrivacyTier.Private,
        };
    }

    private static Recurrence? Repeat(ImportedRepeat? repeat) => repeat switch
    {
        null => null,
        { Every: < 1 } => throw new ArgumentException("A repeat needs \"every\" to be 1 or more."),
        _ => repeat.Unit.Trim().ToLowerInvariant() switch
        {
            "daily" or "day" or "days" => new Recurrence(RecurrenceFrequency.Daily, repeat.Every),
            "weekly" or "week" or "weeks" => new Recurrence(RecurrenceFrequency.Weekly, repeat.Every),
            "monthly" or "month" or "months" => new Recurrence(RecurrenceFrequency.Monthly, repeat.Every),
            _ => throw new ArgumentException($"A repeat's unit must be daily, weekly or monthly, not \"{repeat.Unit}\"."),
        },
    };

    private static string Shorten(string? title) => (title ?? "").Length <= 30 ? title ?? "" : title![..30] + "…";
}
