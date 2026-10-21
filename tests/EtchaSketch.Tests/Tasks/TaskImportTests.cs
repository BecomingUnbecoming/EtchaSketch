using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Tasks;

public sealed class TaskImportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-import-").FullName;
    private readonly SqliteConnection _db;
    private readonly TaskRepository _tasks;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;

    public TaskImportTests()
    {
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        var households = new HouseholdRepository(_db);
        _home = households.Create("Home", "Robin");
        _robin = _home.Members[0];
        _kai = households.AddMember(_home.Id, "Kai");
        _tasks = new TaskRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static string File(params string[] tasks) =>
        $$"""{ "format": "etchasketch-tasks", "version": 1, "tasks": [ {{string.Join(",", tasks)}} ] }""";

    private ImportResult Import(string text, Profile? owner = null, bool dryRun = false) =>
        TaskImport.Apply(_tasks, _home.Id, (owner ?? _robin).Id, TaskImport.Parse(text), dryRun);

    [Fact]
    public void Every_field_that_has_a_home_comes_across()
    {
        Import(File("""
            { "title": "Rotate keys", "notes": "all of them", "due": "2026-10-22", "priority": "high",
              "repeat": { "every": 2, "unit": "weeks" }, "shared": true, "source": "vikunja:41" }
            """));

        var task = Assert.Single(_tasks.VisibleTo(_robin.Id));
        Assert.Equal("Rotate keys", task.Title);
        Assert.Equal(new DateOnly(2026, 10, 22), task.Due);
        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal(new Recurrence(RecurrenceFrequency.Weekly, 2), task.Recurrence);
        Assert.Equal(PrivacyTier.Household, task.Tier);
        Assert.Equal("all of them\n\nImported from vikunja:41", task.Notes);
    }

    [Fact]
    public void Tasks_are_private_to_the_importer_unless_the_file_says_shared()
    {
        Import(File("""{ "title": "Surprise party" }"""));

        Assert.Equal(PrivacyTier.Private, Assert.Single(_tasks.VisibleTo(_robin.Id)).Tier);
        Assert.Empty(_tasks.VisibleTo(_kai.Id));
    }

    [Fact]
    public void Project_labels_and_the_early_reminder_become_real_fields()
    {
        Import(File("""{ "title": "Fix music", "project": "Homelab", "labels": ["b-hands", "quick-win"], "remindDaysBefore": 1, "due": "2026-10-30", "source": "vikunja:40" }"""));

        var task = Assert.Single(_tasks.VisibleTo(_robin.Id));
        Assert.Equal("Homelab", task.Project);
        Assert.Equal(["b-hands", "quick-win"], task.Labels);
        Assert.Equal(1, task.RemindDaysBefore);
        Assert.Equal("Imported from vikunja:40", task.Notes);
    }

    [Fact]
    public void Finished_tasks_are_left_out_and_counted()
    {
        var result = Import(File("""{ "title": "Old", "done": true }""", """{ "title": "New" }"""));

        Assert.Equal((1, 0, 1), (result.Added, result.AlreadyThere, result.Finished));
        Assert.Empty(result.Problems);
        Assert.Equal(["New"], _tasks.VisibleTo(_robin.Id).Select(task => task.Title));
    }

    [Fact]
    public void Importing_the_same_file_twice_adds_nothing_the_second_time()
    {
        var text = File("""{ "title": "A", "source": "vikunja:1" }""", """{ "title": "B", "source": "vikunja:2" }""");
        Import(text);

        var again = Import(text);

        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.AlreadyThere);
        Assert.Equal(2, _tasks.VisibleTo(_robin.Id).Count);
    }

    [Fact]
    public void The_same_source_twice_in_one_file_is_added_once()
    {
        var result = Import(File("""{ "title": "A", "source": "vikunja:1" }""", """{ "title": "A again", "source": "vikunja:1" }"""));

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.AlreadyThere);
    }

    [Fact]
    public void Another_person_importing_the_same_file_gets_their_own_copy()
    {
        var text = File("""{ "title": "A", "source": "vikunja:1" }""");
        Import(text);

        Assert.Equal(1, Import(text, _kai).Added);
    }

    [Fact]
    public void A_preview_changes_nothing()
    {
        var result = Import(File("""{ "title": "A" }"""), dryRun: true);

        Assert.Equal(1, result.Added);
        Assert.Empty(_tasks.VisibleTo(_robin.Id));
    }

    [Fact]
    public void A_bad_task_is_reported_by_number_and_the_rest_still_import()
    {
        var result = Import(File(
            """{ "title": "Fine" }""",
            """{ "title": "" }""",
            """{ "title": "Bad date", "due": "next tuesday" }""",
            """{ "title": "Bad repeat", "repeat": { "every": 1, "unit": "fortnightly" } }""",
            """{ "title": "Zero repeat", "repeat": { "every": 0, "unit": "daily" } }"""));

        Assert.Equal(1, result.Added);
        Assert.Equal(4, result.Problems.Count);
        Assert.StartsWith("Task 2", result.Problems[0]);
        Assert.Contains("isn't a date", result.Problems[1]);
        Assert.Contains("fortnightly", result.Problems[2]);
        Assert.Equal(["Fine"], _tasks.VisibleTo(_robin.Id).Select(task => task.Title));
    }

    [Fact]
    public void An_unknown_priority_counts_as_normal()
    {
        Import(File("""{ "title": "A", "priority": "whenever" }"""));

        Assert.Equal(TaskPriority.Normal, Assert.Single(_tasks.VisibleTo(_robin.Id)).Priority);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "format": "etchasketch-tasks", "version": 2, "tasks": [] }""")]
    [InlineData("""{ "format": "something-else", "version": 1, "tasks": [] }""")]
    [InlineData("[1, 2]")]
    public void Anything_else_is_explained_not_guessed_at(string text)
    {
        Assert.Throws<FormatException>(() => TaskImport.Parse(text));
    }

    [Fact]
    public void Comments_and_trailing_commas_do_not_matter()
    {
        var tasks = TaskImport.Parse("""
            { "format": "etchasketch-tasks", "version": 1, // from Vikunja
              "tasks": [ { "title": "A", }, ] }
            """);

        Assert.Single(tasks);
    }

    [Fact]
    public void A_due_time_and_a_reminder_minutes_before_come_across()
    {
        Import(File("""{ "title": "Vet", "due": "2026-10-30", "dueTime": "14:05", "remindMinutesBefore": 15 }"""));

        var task = Assert.Single(_tasks.VisibleTo(_robin.Id));
        Assert.Equal(new TimeOnly(14, 5), task.DueTime);
        Assert.Equal(15, task.RemindMinutesBefore);
    }

    [Fact]
    public void A_time_it_cant_read_is_reported_and_the_rest_still_go_in()
    {
        var result = Import(File("""{ "title": "Bad", "due": "2026-10-30", "dueTime": "2pm" }""", """{ "title": "Fine" }"""));

        Assert.Equal(1, result.Added);
        Assert.Contains("isn't a time like 14:05", Assert.Single(result.Problems));
    }
}
