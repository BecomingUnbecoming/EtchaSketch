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
            Project = "Health",
            Labels = ["quick-win", "b-hands"],
            RemindDaysBefore = 3,
        };

        _tasks.Add(task);

        var read = _tasks.Get(task.Id)!;
        // Labels is a list, which records compare by reference, so compare it on its own.
        Assert.Equal(task with { Labels = [] }, read with { Labels = [] });
        Assert.Equal(task.Labels, read.Labels);
    }

    [Fact]
    public void Project_and_labels_are_tidied_and_checked()
    {
        var task = _tasks.Add(NewTask("Tidy") with { Project = "  Home  ", Labels = [" a ", "A", "", "b"] });

        Assert.Equal("Home", task.Project);
        Assert.Equal(["a", "b"], task.Labels);
        Assert.Null(_tasks.Add(NewTask("No project") with { Project = "   " }).Project);
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Long") with { Project = new string('x', 61) }));
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Many") with { Labels = Enumerable.Range(0, 21).Select(i => $"l{i}").ToList() }));
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Early") with { RemindDaysBefore = 0 }));
    }

    [Fact]
    public void Project_and_labels_follow_the_tasks_privacy()
    {
        var shared = _tasks.Add(NewTask("Dishes", tier: PrivacyTier.Household) with { Project = "Chores", Labels = ["kitchen"] });
        var secret = _tasks.Add(NewTask("Surprise") with { Project = "Gifts", Labels = ["secret"] });

        Assert.Equal(["Dishes"], _tasks.VisibleTo(_kai.Id).Select(task => task.Title));
        Assert.Equal("Chores", _tasks.VisibleTo(_kai.Id).Single().Project);
        Assert.Null(PrivacyGateway.ForStudio(shared));
        Assert.Null(PrivacyGateway.Project(secret, Audience.ForProfile(_kai.Id)));
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

    [Fact]
    public void A_due_time_its_reminder_and_the_alarm_read_back_and_are_checked()
    {
        var task = _tasks.Add(NewTask("Vet") with
        {
            Due = new DateOnly(2026, 10, 30), DueTime = new TimeOnly(14, 5, 42), RemindMinutesBefore = 15, Alarm = true,
        });

        var read = _tasks.Get(task.Id)!;
        Assert.Equal(new TimeOnly(14, 5), read.DueTime);
        Assert.Equal(15, read.RemindMinutesBefore);
        Assert.True(read.Alarm);
        Assert.Equal(new DateTime(2026, 10, 30, 14, 5, 0), read.DueAt);

        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("No date") with { DueTime = new TimeOnly(9, 0) }));
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("No time") with { Due = new DateOnly(2026, 10, 30), RemindMinutesBefore = 10 }));
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Alarm") with { Due = new DateOnly(2026, 10, 30), Alarm = true }));
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Too early") with
        {
            Due = new DateOnly(2026, 10, 30), DueTime = new TimeOnly(9, 0), RemindMinutesBefore = 24 * 60 + 1,
        }));
    }

    [Fact]
    public void A_subtask_belongs_to_one_task_one_level_deep_and_no_more_open_than_it()
    {
        var parent = _tasks.Add(NewTask("Move house", tier: PrivacyTier.Household));
        var step = _tasks.Add(NewTask("Book van", tier: PrivacyTier.Household) with { ParentId = parent.Id });
        var privateStep = _tasks.Add(NewTask("Pack my desk") with { ParentId = parent.Id });

        Assert.Equal(parent.Id, _tasks.Get(step.Id)!.ParentId);
        Assert.True(_tasks.HasSubtasks(parent.Id));
        Assert.Equal(parent.Id, privateStep.ParentId);

        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Too deep") with { ParentId = step.Id }));
        Assert.Throws<ArgumentException>(() => _tasks.Update(parent with { ParentId = _tasks.Add(NewTask("Other")).Id }));
        var secret = _tasks.Add(NewTask("Surprise"));
        Assert.Throws<ArgumentException>(() => _tasks.Add(NewTask("Shared step", tier: PrivacyTier.Household) with { ParentId = secret.Id }));
        Assert.Throws<ArgumentException>(() => _tasks.Update(parent with { Tier = PrivacyTier.Private }));
    }

    [Fact]
    public void Deleting_a_task_deletes_its_subtasks_links_and_comments()
    {
        var parent = _tasks.Add(NewTask("Move house"));
        var step = _tasks.Add(NewTask("Book van") with { ParentId = parent.Id });
        var other = _tasks.Add(NewTask("Other"));
        _tasks.AddLink(new TaskLink(parent.Id, other.Id, TaskLinkKind.Blocks), Wednesday);
        _tasks.AddComment(parent.Id, _robin.Id, "Ask Sam about the van", Wednesday);

        _tasks.Delete(parent.Id);

        Assert.Null(_tasks.Get(step.Id));
        Assert.Empty(_tasks.LinksOf(other.Id, _robin.Id));
        Assert.Empty(_tasks.OpenBlockersOf(other.Id));
    }

    [Fact]
    public void A_blocked_task_waits_for_its_blocker_unless_you_finish_it_anyway()
    {
        var sdk = _tasks.Add(NewTask("Install the SDK"));
        var apk = _tasks.Add(NewTask("Build the APK"));
        _tasks.AddLink(new TaskLink(sdk.Id, apk.Id, TaskLinkKind.Blocks), Wednesday);

        var blocked = Assert.Throws<TaskBlockedException>(() => _tasks.Complete(apk.Id, _robin.Id, Wednesday));
        Assert.Equal("Install the SDK", Assert.Single(blocked.Blockers).Title);

        _tasks.Complete(sdk.Id, _robin.Id, Wednesday);
        _tasks.Complete(apk.Id, _robin.Id, Wednesday);

        var again = _tasks.Add(NewTask("Second"));
        var blocker = _tasks.Add(NewTask("First"));
        _tasks.AddLink(new TaskLink(blocker.Id, again.Id, TaskLinkKind.Blocks), Wednesday);
        Assert.NotNull(_tasks.Complete(again.Id, _robin.Id, Wednesday, evenIfBlocked: true).CompletedAt);
    }

    [Fact]
    public void Links_read_from_both_sides_never_go_round_in_a_circle_and_are_not_doubled()
    {
        var a = _tasks.Add(NewTask("A"));
        var b = _tasks.Add(NewTask("B"));
        var c = _tasks.Add(NewTask("C"));
        _tasks.AddLink(new TaskLink(a.Id, b.Id, TaskLinkKind.Blocks), Wednesday);
        _tasks.AddLink(new TaskLink(b.Id, c.Id, TaskLinkKind.Blocks), Wednesday);
        _tasks.AddLink(new TaskLink(a.Id, c.Id, TaskLinkKind.Related), Wednesday);
        _tasks.AddLink(new TaskLink(c.Id, a.Id, TaskLinkKind.Related), Wednesday);
        _tasks.AddLink(new TaskLink(a.Id, b.Id, TaskLinkKind.Blocks), Wednesday);

        Assert.Equal([("Blocked by", "A"), ("Blocks", "C")], _tasks.LinksOf(b.Id, _robin.Id).Select(link => (link.Label, link.Other.Title)));
        Assert.Equal([("Blocks", "B"), ("Related", "C")], _tasks.LinksOf(a.Id, _robin.Id).Select(link => (link.Label, link.Other.Title)));
        Assert.Throws<ArgumentException>(() => _tasks.AddLink(new TaskLink(c.Id, a.Id, TaskLinkKind.Blocks), Wednesday));
        Assert.Throws<ArgumentException>(() => _tasks.AddLink(new TaskLink(a.Id, a.Id, TaskLinkKind.Related), Wednesday));

        _tasks.RemoveLink(new TaskLink(a.Id, b.Id, TaskLinkKind.Blocks));
        Assert.Equal([("Blocks", "C")], _tasks.LinksOf(b.Id, _robin.Id).Select(link => (link.Label, link.Other.Title)));
    }

    [Fact]
    public void Links_and_comments_show_only_to_people_who_can_see_the_tasks()
    {
        var mine = _tasks.Add(NewTask("Surprise for Kai"));
        var chore = _tasks.Add(NewTask("Dishes", tier: PrivacyTier.Household));
        _tasks.AddLink(new TaskLink(mine.Id, chore.Id, TaskLinkKind.Related), Wednesday);
        _tasks.AddComment(mine.Id, _robin.Id, "the bakery on 5th", Wednesday);
        _tasks.AddComment(chore.Id, _kai.Id, "I'll do them tonight", Wednesday);

        Assert.Empty(_tasks.LinksOf(chore.Id, _kai.Id));
        Assert.Single(_tasks.LinksOf(chore.Id, _robin.Id));
        Assert.Empty(_tasks.CommentsOf(mine.Id, _kai.Id));
        Assert.Equal("I'll do them tonight", Assert.Single(_tasks.CommentsOf(chore.Id, _robin.Id)).Text);
        Assert.Throws<ArgumentException>(() => _tasks.AddComment(mine.Id, _kai.Id, "peek", Wednesday));
        Assert.Throws<ArgumentException>(() => _tasks.AddComment(chore.Id, _kai.Id, "   ", Wednesday));
    }
}
