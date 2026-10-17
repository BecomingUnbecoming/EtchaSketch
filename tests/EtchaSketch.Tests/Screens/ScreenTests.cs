using System.Globalization;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using EtchaSketch.UI;
using EtchaSketch.UI.ViewModels;

namespace EtchaSketch.Tests.Screens;

/// <summary>Drives the app's screens through their view models, the way a person would tap through them.</summary>
public sealed class ScreenTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-screens-").FullName;
    private readonly InMemoryKeyStore _keys = new();
    private readonly List<MainViewModel> _apps = [];

    // Dates are shown in the person's own language; the tests pin one so they read the same everywhere.
    public ScreenTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    private MainViewModel Launch()
    {
        var app = new MainViewModel(new AppEnvironment(Path.Combine(_directory, "data", "etchasketch.db"), _keys,
            new FixedClock(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero))));
        _apps.Add(app);
        return app;
    }

    private HomeViewModel SetUp(MainViewModel app, string? other = "Jordy")
    {
        var setup = Assert.IsType<SetupViewModel>(app.CurrentPage);
        setup.HouseholdName = "Home";
        setup.YourName = "Lacy";
        setup.OtherName = other;
        setup.CreateCommand.Execute(null);
        return Assert.IsType<HomeViewModel>(app.CurrentPage);
    }

    private static void AddTask(HomeViewModel home, string title, Action<TaskEditorViewModel>? configure = null)
    {
        home.NewTaskCommand.Execute(null);
        var editor = home.Editor!;
        editor.Title = title;
        configure?.Invoke(editor);
        editor.SaveCommand.Execute(null);
        Assert.Null(editor.ErrorMessage);
    }

    [Fact]
    public void First_launch_asks_to_set_up_the_household()
    {
        Assert.IsType<SetupViewModel>(Launch().CurrentPage);
    }

    [Fact]
    public void Setting_up_lands_on_your_home_screen()
    {
        var home = SetUp(Launch());

        Assert.Equal("Hi, Lacy", home.Greeting);
        Assert.Equal(["Lacy", "Jordy", "Anyone"], home.ChoreColumns.Select(column => column.Name));
    }

    [Fact]
    public void Setup_explains_what_is_missing()
    {
        var setup = Assert.IsType<SetupViewModel>(Launch().CurrentPage);
        setup.YourName = " ";

        setup.CreateCommand.Execute(null);

        Assert.Equal("A name is required.", setup.ErrorMessage);
    }

    [Fact]
    public void The_next_launch_goes_straight_home()
    {
        SetUp(Launch());

        Assert.Equal("Hi, Lacy", Assert.IsType<HomeViewModel>(Launch().CurrentPage).Greeting);
    }

    [Fact]
    public void A_new_task_appears_in_your_list_and_leaves_it_when_done()
    {
        var home = SetUp(Launch());

        AddTask(home, "Book dentist", editor =>
        {
            editor.DueDate = new DateTime(2026, 10, 22);
            editor.Priority = TaskPriority.High;
        });

        var row = Assert.Single(home.MyTasks);
        Assert.Equal("Book dentist", row.Title);
        Assert.Equal("Tomorrow", row.DueText);
        Assert.Contains("High", row.Details);
        Assert.Null(home.Editor);

        row.CompleteCommand.Execute(null);

        Assert.Empty(home.MyTasks);
    }

    [Fact]
    public void A_shared_repeating_chore_shows_on_the_board_and_moves_on_when_done()
    {
        var home = SetUp(Launch());

        AddTask(home, "Take out bins", editor =>
        {
            editor.IsShared = true;
            editor.Repeat = editor.RepeatOptions.Single(option => option.Frequency == RecurrenceFrequency.Weekly);
            editor.DueDate = new DateTime(2026, 10, 21);
            editor.Assignee = editor.AssigneeOptions.Single(option => option.Label == "Jordy");
        });

        var jordysColumn = home.ChoreColumns.Single(column => column.Name == "Jordy");
        var chore = Assert.Single(jordysColumn.Chores);
        Assert.Equal("Today", chore.DueText);
        Assert.Contains("Every week", chore.Details);

        chore.CompleteCommand.Execute(null);

        Assert.Equal("Wed 28 Oct", home.ChoreColumns.Single(column => column.Name == "Jordy").Chores.Single().DueText);
    }

    [Fact]
    public void Overdue_tasks_say_so()
    {
        var home = SetUp(Launch());

        AddTask(home, "Renew passport", editor => editor.DueDate = new DateTime(2026, 10, 19));

        Assert.True(home.MyTasks.Single().IsOverdue);
        Assert.Equal("Overdue · Mon 19 Oct", home.MyTasks.Single().DueText);
    }

    [Fact]
    public void Editing_a_task_saves_the_changes()
    {
        var home = SetUp(Launch());
        AddTask(home, "Paint fence");

        home.MyTasks.Single().EditCommand.Execute(null);
        home.Editor!.Title = "Paint the fence";
        home.Editor.SaveCommand.Execute(null);

        Assert.Equal("Paint the fence", home.MyTasks.Single().Title);
    }

    [Fact]
    public void Assigning_a_private_task_to_someone_else_is_explained()
    {
        var home = SetUp(Launch());
        home.NewTaskCommand.Execute(null);
        var editor = home.Editor!;
        editor.Title = "Gift";
        editor.Assignee = editor.AssigneeOptions.Single(option => option.Label == "Jordy");

        editor.SaveCommand.Execute(null);

        Assert.Equal("Share a task with the household before assigning it to someone else.", editor.ErrorMessage);
        Assert.Same(editor, home.Editor);
    }

    [Fact]
    public void Switching_person_shows_their_view_without_the_other_persons_private_tasks_or_notes()
    {
        var app = Launch();
        var lacy = SetUp(app);
        AddTask(lacy, "Lacy's private");
        AddTask(lacy, "Dishes", editor =>
        {
            editor.IsShared = true;
            editor.Notes = "note to self";
        });

        lacy.SwitchPersonCommand.Execute(null);
        var picker = Assert.IsType<ProfilePickerViewModel>(app.CurrentPage);
        picker.People.Single(person => person.Name == "Jordy").ChooseCommand.Execute(null);

        var jordy = Assert.IsType<HomeViewModel>(app.CurrentPage);
        Assert.Equal("Hi, Jordy", jordy.Greeting);
        var dishes = Assert.Single(jordy.MyTasks);
        Assert.Equal("Dishes", dishes.Title);
        Assert.Null(dishes.Notes);
        Assert.False(dishes.CanDelete);
    }

    [Fact]
    public void Someone_can_be_added_from_the_picker()
    {
        var app = Launch();
        SetUp(app, other: null).SwitchPersonCommand.Execute(null);
        var picker = Assert.IsType<ProfilePickerViewModel>(app.CurrentPage);

        picker.NewPersonName = "Jordy";
        picker.AddPersonCommand.Execute(null);

        Assert.Equal(["Lacy", "Jordy"], picker.People.Select(person => person.Name));
    }

    [Fact]
    public void A_lost_key_shows_a_message_instead_of_crashing()
    {
        SetUp(Launch());
        _keys.Delete(EncryptedDatabase.KeyName);

        var message = Assert.IsType<MessageViewModel>(Launch().CurrentPage);
        Assert.Contains("key is missing", message.Detail);
    }

    [Fact]
    public void Without_a_key_store_the_app_says_it_cannot_run()
    {
        using var app = new MainViewModel(environment: null);

        Assert.IsType<MessageViewModel>(app.CurrentPage);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
