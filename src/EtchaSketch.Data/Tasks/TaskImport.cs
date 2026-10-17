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
}

public sealed record ImportedRepeat(int Every, string Unit);

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
        var problems = new List<string>();
        foreach (var (item, number) in imported.Select((item, index) => (item, index + 1)))
        {
            if (item.Done)
            {
                finished++;
                continue;
            }

            var sourceLine = string.IsNullOrWhiteSpace(item.Source) ? null : SourcePrefix + item.Source.Trim();
            if (sourceLine is not null && known.Contains(sourceLine))
            {
                already++;
                continue;
            }

            try
            {
                var task = ToTask(item, sourceLine, householdId, ownerId);
                if (!dryRun)
                {
                    tasks.Add(task);
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

        return new ImportResult(added, already, finished, problems);
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
