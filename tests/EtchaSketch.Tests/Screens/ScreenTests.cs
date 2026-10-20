using System.Globalization;
using EtchaSketch.Core.Tasks;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using EtchaSketch.Data.Notifications;
using EtchaSketch.UI;
using EtchaSketch.UI.Services;
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

    private MainViewModel Launch(IReminderScheduler? scheduler = null)
    {
        var app = new MainViewModel(new AppEnvironment(Path.Combine(_directory, "data", "etchasketch.db"), _keys,
            new FixedClock(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero)), ReminderScheduler: scheduler));
        _apps.Add(app);
        return app;
    }

    private HomeViewModel SetUp(MainViewModel app, string? other = "Kai")
    {
        var setup = Assert.IsType<SetupViewModel>(app.CurrentPage);
        setup.HouseholdName = "Home";
        setup.YourName = "Robin";
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

        Assert.Equal("Hi, Robin", home.Greeting);
        Assert.Equal(["Robin", "Kai", "Anyone"], home.ChoreColumns.Select(column => column.Name));
    }

    [Fact]
    public void Importing_a_file_previews_then_adds_the_tasks_to_my_list()
    {
        var home = SetUp(Launch());
        home.OpenImportCommand.Execute(null);
        var import = home.Import!;
        import.FileText = """
            { "format": "etchasketch-tasks", "version": 1, "tasks": [
              { "title": "Rotate keys", "source": "vikunja:41" },
              { "title": "Old thing", "done": true } ] }
            """;

        import.PreviewCommand.Execute(null);
        Assert.Equal("Ready to add 1 task, 1 finished left out.", import.Summary);
        Assert.Empty(home.MyTasks);

        Assert.True(import.IsChoosing);
        import.ImportCommand.Execute(null);
        Assert.Equal("Added 1 task, 1 finished left out.", import.Summary);
        Assert.False(import.ImportCommand.CanExecute(null));

        // The panel says it is finished, and the list behind it is already up to date.
        Assert.True(import.IsFinished);
        Assert.False(import.IsChoosing);
        Assert.Equal(["Rotate keys"], home.MyTasks.Select(task => task.Title));

        import.CloseCommand.Execute(null);
        Assert.Null(home.Import);
        Assert.Equal(["Rotate keys"], home.MyTasks.Select(task => task.Title));
    }

    [Fact]
    public void A_file_that_is_not_a_task_file_is_explained()
    {
        var home = SetUp(Launch());
        home.OpenImportCommand.Execute(null);
        home.Import!.FileText = "hello";

        home.Import.PreviewCommand.Execute(null);

        Assert.StartsWith("That isn't a task file", home.Import.ErrorMessage);
        Assert.False(home.Import.ImportCommand.CanExecute(null));
    }

    [Fact]
    public void A_task_keeps_its_project_labels_and_early_reminder_and_the_list_can_be_filtered()
    {
        var home = SetUp(Launch());
        home.NewTaskCommand.Execute(null);
        home.Editor!.Title = "Rotate keys";
        home.Editor.Project = "Homelab";
        home.Editor.LabelsText = "b-hands, security";
        home.Editor.Reminder = home.Editor.ReminderOptions.Single(option => option.Days == 1);
        home.Editor.DueDate = new DateTime(2026, 10, 30);
        home.Editor.SaveCommand.Execute(null);
        home.NewTaskCommand.Execute(null);
        home.Editor!.Title = "Buy milk";
        home.Editor.SaveCommand.Execute(null);

        var row = home.MyTasks.Single(task => task.Title == "Rotate keys");
        Assert.Contains("Homelab", row.Details);
        Assert.Contains("#b-hands #security", row.Details);

        home.FilterText = "security";
        Assert.Equal(["Rotate keys"], home.MyTasks.Select(task => task.Title));
        home.FilterText = "#milk";
        Assert.Empty(home.MyTasks);
        home.FilterText = "MILK";
        Assert.Equal(["Buy milk"], home.MyTasks.Select(task => task.Title));
        home.FilterText = "";
        Assert.Equal(2, home.MyTasks.Count);

        row.EditCommand.Execute(null);
        Assert.Equal("Homelab", home.Editor!.Project);
        Assert.Equal("b-hands, security", home.Editor.LabelsText);
        Assert.Equal(1, home.Editor.Reminder.Days);
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

        Assert.Equal("Hi, Robin", Assert.IsType<HomeViewModel>(Launch().CurrentPage).Greeting);
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
            editor.Assignee = editor.AssigneeOptions.Single(option => option.Label == "Kai");
        });

        var kaisColumn = home.ChoreColumns.Single(column => column.Name == "Kai");
        var chore = Assert.Single(kaisColumn.Chores);
        Assert.Equal("Today", chore.DueText);
        Assert.Contains("Every week", chore.Details);

        chore.CompleteCommand.Execute(null);

        Assert.Equal("Wed 28 Oct", home.ChoreColumns.Single(column => column.Name == "Kai").Chores.Single().DueText);
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
        editor.Assignee = editor.AssigneeOptions.Single(option => option.Label == "Kai");

        editor.SaveCommand.Execute(null);

        Assert.Equal("Share a task with the household before assigning it to someone else.", editor.ErrorMessage);
        Assert.Same(editor, home.Editor);
    }

    [Fact]
    public void Switching_person_shows_their_view_without_the_other_persons_private_tasks_or_notes()
    {
        var app = Launch();
        var robin = SetUp(app);
        AddTask(robin, "Robin's private");
        AddTask(robin, "Dishes", editor =>
        {
            editor.IsShared = true;
            editor.Notes = "note to self";
        });

        robin.SwitchPersonCommand.Execute(null);
        var picker = Assert.IsType<ProfilePickerViewModel>(app.CurrentPage);
        picker.People.Single(person => person.Name == "Kai").ChooseCommand.Execute(null);

        var kai = Assert.IsType<HomeViewModel>(app.CurrentPage);
        Assert.Equal("Hi, Kai", kai.Greeting);
        var dishes = Assert.Single(kai.MyTasks);
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

        picker.NewPersonName = "Kai";
        picker.AddPersonCommand.Execute(null);

        Assert.Equal(["Robin", "Kai"], picker.People.Select(person => person.Name));
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

    [Fact]
    public void A_task_due_today_lights_up_the_notifications_button()
    {
        var home = SetUp(Launch());
        Assert.Equal("Notifications", home.NotificationsLabel);

        AddTask(home, "Book dentist", editor => editor.DueDate = new DateTime(2026, 10, 21));

        Assert.Equal("Notifications (1)", home.NotificationsLabel);
    }

    [Fact]
    public void Opening_notifications_shows_them_and_marks_them_read()
    {
        var home = SetUp(Launch());
        AddTask(home, "Renew passport", editor => editor.DueDate = new DateTime(2026, 10, 19));

        home.OpenNotificationsCommand.Execute(null);

        var center = home.Notifications!;
        var row = Assert.Single(center.Items);
        Assert.Equal("Renew passport is overdue", row.Title);
        Assert.Equal("It was due Mon 19 Oct", row.Body);
        Assert.Equal("09:00", row.WhenText);
        Assert.True(row.IsUnread);

        center.CloseCommand.Execute(null);

        Assert.Null(home.Notifications);
        Assert.Equal("Notifications", home.NotificationsLabel);
    }

    [Fact]
    public void Quiet_hours_are_saved_and_held_notifications_are_counted()
    {
        var home = SetUp(Launch());
        home.OpenNotificationsCommand.Execute(null);
        var center = home.Notifications!;
        Assert.False(center.QuietHoursEnabled);

        center.QuietStart = new TimeSpan(8, 0, 0);
        center.QuietEnd = new TimeSpan(10, 0, 0);
        center.QuietHoursEnabled = true;
        center.CloseCommand.Execute(null);
        AddTask(home, "Book dentist", editor => editor.DueDate = new DateTime(2026, 10, 21));

        Assert.Equal("Notifications", home.NotificationsLabel);
        home.OpenNotificationsCommand.Execute(null);
        Assert.Empty(home.Notifications!.Items);
        Assert.Equal("1 notification held until 10:00", home.Notifications.HeldText);
        Assert.True(home.Notifications.QuietHoursEnabled);
        Assert.Equal(new TimeSpan(8, 0, 0), home.Notifications.QuietStart);
    }

    [Fact]
    public void A_chore_given_to_someone_shows_in_their_notifications()
    {
        var app = Launch();
        var robin = SetUp(app);
        AddTask(robin, "Dishes", editor =>
        {
            editor.IsShared = true;
            editor.Assignee = editor.AssigneeOptions.Single(option => option.Label == "Kai");
        });
        Assert.Equal("Notifications", robin.NotificationsLabel);

        robin.SwitchPersonCommand.Execute(null);
        Assert.IsType<ProfilePickerViewModel>(app.CurrentPage).People.Single(person => person.Name == "Kai")
            .ChooseCommand.Execute(null);

        var kai = Assert.IsType<HomeViewModel>(app.CurrentPage);
        kai.OpenNotificationsCommand.Execute(null);
        Assert.Equal("Robin assigned Dishes to you", Assert.Single(kai.Notifications!.Items).Title);
    }

    [Fact]
    public void A_task_keeps_its_time_reminder_and_alarm_and_loses_them_with_its_date()
    {
        var home = SetUp(Launch());
        AddTask(home, "Vet", editor =>
        {
            editor.DueDate = new DateTime(2026, 10, 22);
            editor.DueTime = new TimeSpan(14, 5, 0);
            editor.RemindBefore = editor.MinutesOptions.Single(option => option.Minutes == 30);
            editor.IsAlarm = true;
        });

        home.MyTasks.Single().EditCommand.Execute(null);
        var editor = home.Editor!;
        Assert.Equal(new TimeSpan(14, 5, 0), editor.DueTime);
        Assert.Equal(30, editor.RemindBefore.Minutes);
        Assert.True(editor.IsAlarm);

        editor.DueDate = null;
        editor.SaveCommand.Execute(null);
        Assert.Null(editor.ErrorMessage);

        home.MyTasks.Single().EditCommand.Execute(null);
        Assert.Null(home.Editor!.DueTime);
        Assert.False(home.Editor.IsAlarm);
    }

    [Fact]
    public void A_reminder_can_be_snoozed_from_the_feed()
    {
        var home = SetUp(Launch());
        AddTask(home, "Take medicine", editor =>
        {
            editor.DueDate = new DateTime(2026, 10, 21);
            editor.DueTime = new TimeSpan(8, 30, 0);
        });
        home.CheckRemindersCommand.Execute(null);

        home.OpenNotificationsCommand.Execute(null);
        var feed = home.Notifications!;
        var row = Assert.Single(feed.Items);
        Assert.Equal("Take medicine is due now", row.Title);

        row.SnoozeTenMinutesCommand.Execute(null);

        Assert.Empty(feed.Items);
        Assert.Equal("Snoozed until 09:10: Take medicine is due now", feed.SnoozedText);
    }

    private sealed class FakeAlarmClock : IReminderScheduler
    {
        public IReadOnlyList<PlannedReminder> Scheduled { get; private set; } = [];

        public void Replace(IReadOnlyList<PlannedReminder> reminders) => Scheduled = reminders;
    }

    [Fact]
    public void The_phones_alarm_clock_gets_the_current_persons_upcoming_reminders_only()
    {
        var alarms = new FakeAlarmClock();
        var home = SetUp(Launch(alarms));
        AddTask(home, "Vet", editor =>
        {
            editor.DueDate = new DateTime(2026, 10, 21);
            editor.DueTime = new TimeSpan(15, 0, 0);
            editor.IsAlarm = true;
        });
        AddTask(home, "Kai's dishes", editor =>
        {
            editor.IsShared = true;
            editor.DueDate = new DateTime(2026, 10, 21);
            editor.DueTime = new TimeSpan(16, 0, 0);
            editor.Assignee = editor.AssigneeOptions.Single(option => option.Label == "Kai");
        });

        var vet = Assert.Single(alarms.Scheduled, reminder => reminder.Kind == Core.Notifications.NotificationKind.DueNow);
        Assert.Equal("Vet is due now", vet.Title);
        Assert.True(vet.Alarm);
        Assert.DoesNotContain(alarms.Scheduled, reminder => reminder.Title.Contains("dishes", StringComparison.Ordinal));
    }

    [Fact]
    public void Done_on_a_phone_notification_completes_the_task_once_someone_is_signed_in()
    {
        var home = SetUp(Launch());
        AddTask(home, "Take medicine");
        var taskId = home.MyTasks.Single().Id;

        ReminderActions.RequestComplete(Guid.NewGuid());
        ReminderActions.RequestComplete(taskId);
        home.CompleteRequestedTasks();

        Assert.Empty(home.MyTasks);
        Assert.Empty(ReminderActions.TakeCompletions());
    }

    [Fact]
    public void Export_builds_each_kind_of_file_with_a_dated_name()
    {
        var home = SetUp(Launch());
        AddTask(home, "Vet", editor => editor.DueDate = new DateTime(2026, 10, 30));

        home.OpenExportCommand.Execute(null);
        var export = home.Export!;

        Assert.Equal("etchasketch-robin-2026-10-21.json", export.FileName(ExportKind.Everything));
        Assert.Contains("\"title\": \"Vet\"", export.Build(ExportKind.Everything), StringComparison.Ordinal);
        Assert.StartsWith("Title,Done,Due", export.Build(ExportKind.Spreadsheet), StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Vet", export.Build(ExportKind.Calendar), StringComparison.Ordinal);

        export.CloseCommand.Execute(null);
        Assert.False(home.IsShowingExport);
    }

    [Fact]
    public void A_step_shows_under_its_task_and_the_task_counts_its_steps()
    {
        var home = SetUp(Launch());
        AddTask(home, "Move house");
        AddTask(home, "Another task");
        AddTask(home, "Book van", editor => editor.Parent = editor.ParentOptions.Single(option => option.Label == "Move house"));

        Assert.Equal(["Move house", "Book van", "Another task"], home.MyTasks.Select(row => row.Title));
        Assert.True(home.MyTasks[1].IsStep);
        Assert.Contains("1 step to do", home.MyTasks[0].Details, StringComparison.Ordinal);

        home.MyTasks[0].EditCommand.Execute(null);
        Assert.False(home.Editor!.CanBeStep);
    }

    [Fact]
    public void A_task_waiting_on_another_asks_before_it_is_marked_done()
    {
        var home = SetUp(Launch());
        AddTask(home, "Install the SDK");
        AddTask(home, "Build the APK");

        home.MyTasks.Single(row => row.Title == "Build the APK").EditCommand.Execute(null);
        var editor = home.Editor!;
        editor.NewLinkKind = editor.LinkKinds.Single(kind => kind.Label == "Is blocked by");
        editor.NewLinkTarget = editor.LinkTargets.Single(target => target.Label == "Install the SDK");
        editor.AddLinkCommand.Execute(null);
        Assert.Equal(["Blocked by · Install the SDK"], editor.Links.Select(link => link.Text));
        editor.CancelCommand.Execute(null);

        var apk = home.MyTasks.Single(row => row.Title == "Build the APK");
        Assert.Equal("Waiting on Install the SDK", apk.WaitingText);
        apk.CompleteCommand.Execute(null);
        Assert.Equal("Build the APK is waiting on Install the SDK. Mark it done anyway?", home.BlockedQuestion);
        Assert.Equal(2, home.MyTasks.Count);

        home.CompleteAnywayCommand.Execute(null);
        Assert.Equal(["Install the SDK"], home.MyTasks.Select(row => row.Title));
        Assert.False(home.IsAskingAboutBlocked);
    }

    [Fact]
    public void Comments_are_added_from_the_editor()
    {
        var home = SetUp(Launch());
        AddTask(home, "Dishes", editor => editor.IsShared = true);

        home.MyTasks.Single().EditCommand.Execute(null);
        home.Editor!.NewComment = "I'll do them tonight";
        home.Editor.AddCommentCommand.Execute(null);

        var comment = Assert.Single(home.Editor.Comments);
        Assert.Equal("I'll do them tonight", comment.Text);
        Assert.StartsWith("Robin · ", comment.Byline, StringComparison.Ordinal);
        Assert.Null(home.Editor.NewComment);
    }

    [Fact]
    public void Link_lines_from_an_earlier_import_can_be_turned_into_links_from_the_import_panel()
    {
        var home = SetUp(Launch());
        AddTask(home, "Install the SDK");
        AddTask(home, "Build the APK", editor => editor.Notes = "Blocked by: Install the SDK");

        home.OpenImportCommand.Execute(null);
        home.Import!.ConvertLinkLinesCommand.Execute(null);

        Assert.Equal("Made 1 link.", home.Import.LinkLinesResult);
        Assert.Equal("Waiting on Install the SDK", home.MyTasks.Single(row => row.Title == "Build the APK").WaitingText);
    }
}
