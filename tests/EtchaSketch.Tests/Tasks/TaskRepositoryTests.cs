using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Tasks;

public sealed class TaskRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-tasks-").FullName;
    private readonly SqliteConnection _db;
    private readonly TaskRepository _tasks;
    private readonly Household _home;
    private readonly Profile _robin;
    private readonly Profile _kai;

    public TaskRepositoryTests()
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

    private TaskItem NewTask(string title, Profile? owner = null, PrivacyTier tier = PrivacyTier.Private) => new()
    {
        HouseholdId = _home.Id,
        OwnerId = (owner ?? _robin).Id,
        Title = title,
        Tier = tier,
    };

    private TaskItem NewChore(string title, Profile? assignee = null, Recurrence? recurrence = null, DateOnly? due = null) =>
        NewTask(title, tier: PrivacyTier.Household) with
        {
            AssigneeId = assignee?.Id,
            Recurrence = recurrence ?? new Recurrence(RecurrenceFrequency.Weekly),
            Due = due,
        };

    [Fact]
    public void A_task_reads_back_with_every_field()
    {
        var task = NewTask("Book dentist") with
        {
            Notes = "ask about the crown",
            Due = new DateOnly(2026, 10, 30),
            Priority = TaskPriority.High,
            AssigneeId = _robin.Id,
            Recurrence = new Recurrence(RecurrenceFrequency.Monthly, 6),
        };

        _tasks.Add(task);

        Assert.Equal(task, _tasks.Get(task.Id));
    }

    [Fact]
    public void Edits_are_saved()
    {
        var task = _tasks.Add(NewTask("Paint fence"));

        _tasks.Update(task with { Title = "Paint the fence", Priority = TaskPriority.Low });

        var saved = _tasks.Get(task.Id)!;
        Assert.Equal("Paint the fence", saved.Title);
        Assert.Equal(TaskPriority.Low, saved.Priority);
    }

    [Fact]
    public void Updating_a_missing_task_fails()
    {
        Assert.Throws<ArgumentException>(() => _tasks.Update(NewTask("Ghost")));
    }

    [Fact]
    public void Deleted_tasks_are_gone()
    {
        var task = _tasks.Add(NewTask("Old idea"));

        _tasks.Delete(task.Id);

        Assert.Null(_tasks.Get(task.Id));
    }

    [Fact]
    public void Titles_are_trimmed_and_required()
    {
        Assert.Equal("Laundry", _tasks.Add(NewTask("  Laundry ")).Title);
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("   ")));
    }

    [Fact]
    public void Completing_a_one_off_task_closes_it()
    {
        var task = _tasks.Add(NewTask("Return library books"));

        var done = _tasks.Complete(task.Id, _robin.Id, Wednesday);

        Assert.Equal(Wednesday, done.CompletedAt);
        Assert.DoesNotContain(_tasks.VisibleTo(_robin.Id), t => t.Id == task.Id);
    }

    [Fact]
    public void Completing_a_recurring_chore_moves_it_to_the_next_due_date_and_logs_who_did_it()
    {
        var chore = _tasks.Add(NewChore("Take out bins", due: new DateOnly(2026, 10, 19)));

        var next = _tasks.Complete(chore.Id, _kai.Id, Wednesday);

        Assert.Null(next.CompletedAt);
        Assert.Equal(new DateOnly(2026, 10, 26), next.Due);
        var log = Assert.Single(_tasks.Completions(chore.Id));
        Assert.Equal(_kai.Id, log.ProfileId);
        Assert.Equal(Wednesday, log.CompletedAt);
    }

    [Fact]
    public void A_recurring_chore_with_no_due_date_is_next_due_one_interval_after_completion()
    {
        var chore = _tasks.Add(NewChore("Water plants", recurrence: new Recurrence(RecurrenceFrequency.Daily, 3)));

        Assert.Equal(new DateOnly(2026, 10, 24), _tasks.Complete(chore.Id, _robin.Id, Wednesday).Due);
    }

    [Fact]
    public void Each_person_sees_their_own_tasks_and_shared_ones_but_not_others_private_tasks()
    {
        var robinsPrivate = _tasks.Add(NewTask("Robin's private"));
        var kaisPrivate = _tasks.Add(NewTask("Kai's private", _kai));
        var shared = _tasks.Add(NewChore("Dishes"));

        Assert.Equal(new[] { robinsPrivate.Id, shared.Id }.Order(), _tasks.VisibleTo(_robin.Id).Select(t => t.Id).Order());
        Assert.Equal(new[] { kaisPrivate.Id, shared.Id }.Order(), _tasks.VisibleTo(_kai.Id).Select(t => t.Id).Order());
    }

    [Fact]
    public void Notes_on_someone_elses_shared_chore_are_never_read_out()
    {
        _tasks.Add(NewChore("Dishes") with { Notes = "Robin's note to self" });

        Assert.Equal("Robin's note to self", Assert.Single(_tasks.VisibleTo(_robin.Id)).Notes);
        Assert.Null(Assert.Single(_tasks.VisibleTo(_kai.Id)).Notes);
    }

    [Fact]
    public void Tasks_are_ordered_by_due_date_then_priority_with_undated_last()
    {
        _tasks.Add(NewTask("someday") with { Priority = TaskPriority.Urgent });
        _tasks.Add(NewTask("friday low") with { Due = new DateOnly(2026, 10, 23), Priority = TaskPriority.Low });
        _tasks.Add(NewTask("friday high") with { Due = new DateOnly(2026, 10, 23), Priority = TaskPriority.High });
        _tasks.Add(NewTask("thursday") with { Due = new DateOnly(2026, 10, 22) });

        Assert.Equal(["thursday", "friday high", "friday low", "someday"], _tasks.VisibleTo(_robin.Id).Select(t => t.Title));
    }

    [Fact]
    public void Chore_board_groups_shared_chores_by_person_with_unassigned_last()
    {
        _tasks.Add(NewChore("Bins", _kai));
        _tasks.Add(NewChore("Dishes", _robin));
        _tasks.Add(NewChore("Mop"));
        _tasks.Add(NewTask("Robin's private", _robin) with { AssigneeId = _robin.Id });

        var board = _tasks.ChoreBoard(_home.Id);

        Assert.Equal([_robin.Id, _kai.Id, null], board.Select(column => column.AssigneeId));
        Assert.Equal(["Dishes"], board[0].Chores.Select(c => c.Title));
        Assert.Equal(["Bins"], board[1].Chores.Select(c => c.Title));
        Assert.Equal(["Mop"], board[2].Chores.Select(c => c.Title));
    }

    [Fact]
    public void A_private_task_cannot_be_assigned_to_someone_who_cannot_see_it()
    {
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Secret gift") with { AssigneeId = _kai.Id }));
    }

    [Fact]
    public void Assignees_must_be_in_the_same_household()
    {
        var neighbours = new HouseholdRepository(_db).Create("Next door", "Sam");

        Assert.Throws<ArgumentException>(() => _tasks.Add(NewChore("Bins", neighbours.Members[0])));
    }
}
