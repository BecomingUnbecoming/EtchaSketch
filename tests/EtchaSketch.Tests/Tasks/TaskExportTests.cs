using System.Text;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Tasks;

public sealed class TaskExportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-export-").FullName;
    private readonly List<SqliteConnection> _connections = [];
    private readonly TaskRepository _tasks;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;

    public TaskExportTests()
    {
        (_tasks, _home) = NewDevice("desktop", "Robin");
        _robin = _home.Members[0];
        _kai = new HouseholdRepository(_connections[0]).AddMember(_home.Id, "Kai");
        _home = new HouseholdRepository(_connections[0]).Get(_home.Id)!;
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    private (TaskRepository Tasks, Household Home) NewDevice(string name, string person)
    {
        Directory.CreateDirectory(Path.Combine(_directory, name));
        var db = EncryptedDatabase.Open(Path.Combine(_directory, name, "etchasketch.db"), new InMemoryKeyStore());
        _connections.Add(db);
        Schema.Apply(db);
        return (new TaskRepository(db), new HouseholdRepository(db).Create("Home", person));
    }

    private TaskItem Add(string title, Profile? owner = null, PrivacyTier tier = PrivacyTier.Private, string? notes = null) =>
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = (owner ?? _robin).Id, Title = title, Tier = tier, Notes = notes });

    [Fact]
    public void Importing_your_export_on_a_fresh_install_brings_back_the_same_tasks()
    {
        var everything = _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Vet", Notes = "ask about the crown", Due = new DateOnly(2026, 10, 30),
            DueTime = new TimeOnly(14, 5), RemindMinutesBefore = 30, RemindDaysBefore = 2, Alarm = true, Priority = TaskPriority.High,
            Recurrence = new Recurrence(RecurrenceFrequency.Weekly, 2), Project = "Health", Labels = ["b-hands", "quick-win"],
            Tier = PrivacyTier.Household,
        });
        var plain = Add("Plain");
        var finished = Add("Finished");
        _tasks.Complete(finished.Id, _robin.Id, Now);

        var json = TaskExport.ToJson(_tasks, _robin, Now);
        var (fresh, freshHome) = NewDevice("new-laptop", "Robin");
        var me = freshHome.Members[0];
        var result = TaskImport.Apply(fresh, freshHome.Id, me.Id, TaskImport.Parse(json));

        Assert.Equal((2, 0, 1), (result.Added, result.AlreadyThere, result.Finished));
        Assert.Empty(result.Problems);
        foreach (var original in new[] { everything, plain })
        {
            var back = fresh.Get(original.Id)!;
            Assert.Equal(original with { HouseholdId = freshHome.Id, OwnerId = me.Id, Labels = [] }, back with { Labels = [] });
            Assert.Equal(original.Labels, back.Labels);
        }
    }

    [Fact]
    public void Importing_your_export_again_adds_nothing()
    {
        Add("Plain");

        var result = TaskImport.Apply(_tasks, _home.Id, _robin.Id, TaskImport.Parse(TaskExport.ToJson(_tasks, _robin, Now)));

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.AlreadyThere);
        Assert.Single(_tasks.VisibleTo(_robin.Id));
    }

    [Fact]
    public void An_export_never_holds_someone_elses_private_tasks_or_the_notes_on_their_shared_ones()
    {
        Add("Surprise party for Robin", owner: _kai, notes: "the bakery on 5th");
        var shared = Add("Dishes", owner: _kai, tier: PrivacyTier.Household, notes: "Kai's own note");
        _tasks.Update(shared with { Due = new DateOnly(2026, 10, 30) });
        Add("Mine", notes: "my note");

        var exports = string.Join("\n",
            TaskExport.ToJson(_tasks, _robin, Now), TaskExport.ToCsv(_tasks, _home, _robin), TaskExport.ToIcs(_tasks, _robin, Now));

        Assert.DoesNotContain("Surprise", exports, StringComparison.Ordinal);
        Assert.DoesNotContain("bakery", exports, StringComparison.Ordinal);
        Assert.DoesNotContain("Kai's own note", exports, StringComparison.Ordinal);
        Assert.Contains("Dishes", exports, StringComparison.Ordinal);
        Assert.Contains("my note", exports, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spreadsheet_quotes_what_needs_it_and_defuses_formulas()
    {
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "=HYPERLINK(\"x\")", Notes = "line one\nline, two", Labels = ["a", "b"],
            Due = new DateOnly(2026, 10, 30), DueTime = new TimeOnly(9, 30), AssigneeId = _robin.Id,
        });

        var lines = TaskExport.ToCsv(_tasks, _home, _robin).Split("\r\n");

        Assert.Equal("Title,Done,Due,Time,Priority,Project,Labels,Repeats,Shared,Who's doing it,Notes", lines[0]);
        Assert.StartsWith("\"'=HYPERLINK(\"\"x\"\")\",,2026-10-30,09:30,Normal,,a; b,,no,Robin,\"line one\nline, two\"", lines[1]);
    }

    [Fact]
    public void The_calendar_file_has_timed_all_day_repeating_and_reminder_events()
    {
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Vet; then shop, maybe", Due = new DateOnly(2026, 10, 30),
            DueTime = new TimeOnly(14, 5), RemindMinutesBefore = 30, Recurrence = new Recurrence(RecurrenceFrequency.Monthly, 1),
        });
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Bins", Due = new DateOnly(2026, 10, 22) });
        _tasks.Add(new TaskItem { HouseholdId = _home.Id, OwnerId = _robin.Id, Title = "Someday" });
        _tasks.Add(new TaskItem
        {
            HouseholdId = _home.Id, OwnerId = _robin.Id, Due = new DateOnly(2026, 10, 23),
            Title = "A very long title that goes on and on past the seventy-five byte limit of a calendar line ✨✨",
        });

        var ics = TaskExport.ToIcs(_tasks, _robin, Now);

        Assert.Equal(3, ics.Split("BEGIN:VEVENT").Length - 1);
        Assert.Contains("DTSTART:20261030T140500\r\n", ics, StringComparison.Ordinal);
        Assert.Contains(@"SUMMARY:Vet\; then shop\, maybe" + "\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("RRULE:FREQ=MONTHLY;INTERVAL=1\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT30M\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;VALUE=DATE:20261022\r\n", ics, StringComparison.Ordinal);
        Assert.All(ics.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Contains("seventy-five byte limit of a calendar line ✨✨", ics.Replace("\r\n ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
