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
[Collection(AppScreens.Name)]
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

    private MainViewModel Launch(IReminderScheduler? scheduler = null, Core.Pantry.IProductLookup? products = null, IBarcodeScanner? scanner = null,
        Func<Uri, string, Func<string, Task<System.Text.Json.JsonElement>>>? grocy = null)
    {
        var app = new MainViewModel(new AppEnvironment(Path.Combine(_directory, "data", "etchasketch.db"), _keys,
            new FixedClock(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero)), ReminderScheduler: scheduler,
            ProductLookup: products ?? new FakeProducts(), BarcodeScanner: scanner, GrocyReader: grocy));
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

    [Fact]
    public void Stock_can_be_added_used_edited_and_sent_to_the_shopping_list()
    {
        var home = SetUp(Launch());
        var pantry = home.Pantry;
        pantry.Name = "Milk";
        pantry.Amount = 2;
        pantry.Unit = "bottles";
        pantry.Location = "Fridge";
        pantry.BestBefore = new DateTime(2026, 10, 23);
        pantry.LowAt = 1;
        pantry.SaveCommand.Execute(null);

        var milk = Assert.Single(pantry.Items);
        Assert.Equal(("Milk", "2 bottles"), (milk.Name, milk.AmountText));
        Assert.Equal("Fridge · goes off Fri 23 Oct", milk.Details);
        Assert.True(milk.IsExpiring);

        milk.UseCommand.Execute(null);
        Assert.Equal("1 bottles", pantry.Items.Single().AmountText);
        Assert.Contains("running low", pantry.Items.Single().Details, StringComparison.Ordinal);

        pantry.AddLowToListCommand.Execute(null);
        Assert.Equal("Added 1 thing to the shopping list.", pantry.Message);
        Assert.Equal(["Milk"], home.Shopping.Lines.Select(line => line.Name));

        pantry.Items.Single().EditCommand.Execute(null);
        Assert.True(pantry.IsEditing);
        pantry.Name = "Oat milk";
        pantry.SaveCommand.Execute(null);
        Assert.Equal("Oat milk", pantry.Items.Single().Name);
        Assert.False(pantry.IsEditing);
    }

    [Fact]
    public void The_shopping_list_ticks_things_off_and_puts_stock_back()
    {
        var home = SetUp(Launch());
        home.Pantry.Name = "Rice";
        home.Pantry.Amount = 0;
        home.Pantry.SaveCommand.Execute(null);
        home.Pantry.Items.Single().AddToListCommand.Execute(null);
        home.Shopping.NewLine = "Bread";
        home.Shopping.AddCommand.Execute(null);

        Assert.Equal(["Rice", "Bread"], home.Shopping.Lines.Select(line => line.Name));
        home.Shopping.Lines.Single(line => line.Name == "Rice").ToggleCommand.Execute(null);

        Assert.True(home.Shopping.Lines.Single(line => line.Name == "Rice").IsBought);
        Assert.Equal("1", home.Pantry.Items.Single().AmountText);
        home.Shopping.ClearBoughtCommand.Execute(null);
        Assert.Equal(["Bread"], home.Shopping.Lines.Select(line => line.Name));
    }

    [Fact]
    public void Something_about_to_go_off_shows_in_the_notifications()
    {
        var home = SetUp(Launch());
        home.Pantry.Name = "Yoghurt";
        home.Pantry.BestBefore = new DateTime(2026, 10, 22);
        home.Pantry.SaveCommand.Execute(null);

        home.OpenNotificationsCommand.Execute(null);

        Assert.Contains(home.Notifications!.Items, row => row.Title == "Yoghurt goes off tomorrow");
    }

    [Fact]
    public void A_recipe_is_added_planned_and_cooked_from_the_meals_tab()
    {
        var home = SetUp(Launch());
        home.Pantry.Name = "Rice";
        home.Pantry.Amount = 1000;
        home.Pantry.Unit = "g";
        home.Pantry.SaveCommand.Execute(null);

        var meals = home.Meals;
        meals.RecipeName = "Fried rice";
        meals.RecipeServings = 2;
        meals.RecipeIngredients = "300 g rice\n2 eggs\nsoy sauce";
        meals.SaveRecipeCommand.Execute(null);
        Assert.Equal(("Fried rice", "2 servings · 3 ingredients"), (meals.Recipes.Single().Name, meals.Recipes.Single().Summary));

        meals.PlanRecipe = meals.RecipeChoices.Single(choice => choice.Label == "Fried rice");
        meals.PlanCommand.Execute(null);
        Assert.Equal("Fried rice is planned. Added to the shopping list: eggs, soy sauce.", meals.Message);
        Assert.Equal(["eggs", "soy sauce"], home.Shopping.Lines.Select(line => line.Name));

        var today = meals.Week[0];
        Assert.Equal(("Today", "Dinner: Fried rice"), (today.Day, today.Meals.Single().Text));
        today.Meals.Single().CookCommand.Execute(null);
        Assert.True(home.Meals.Week[0].Meals.Single().IsCooked);
        Assert.Equal("700 g", home.Pantry.Items.Single().AmountText);
    }

    [Fact]
    public void Things_can_be_added_searched_lent_out_and_returned()
    {
        var home = SetUp(Launch());
        var things = home.Inventory;
        Assert.False(things.IsFormOpen);
        things.OpenFormCommand.Execute(null);
        things.Name = "Drill";
        things.Location = "Garage";
        things.Category = "Tools";
        things.Serial = "DW-123";
        things.WarrantyEnds = new DateTime(2027, 3, 1);
        things.WhoHasIt = things.WhoHasItOptions.Single(option => option.Label == "Kai");
        things.SaveCommand.Execute(null);
        Assert.False(things.IsFormOpen);

        things.OpenFormCommand.Execute(null);
        things.Name = "Blender";
        things.Location = "Kitchen";
        things.SaveCommand.Execute(null);

        Assert.Equal(["Garage", "Kitchen"], things.Items.Select(row => row.Details.Split(" · ")[0]));
        var drill = things.Items.Single(row => row.Name == "Drill");
        Assert.Equal("Garage · Tools · serial DW-123 · warranty until 1 Mar 2027", drill.Details);
        Assert.Equal("With Kai", drill.WithText);

        things.Search = "dw-1";
        Assert.Equal(["Drill"], things.Items.Select(row => row.Name));
        things.Search = null;

        drill.ReturnCommand.Execute(null);
        Assert.Null(things.Items.Single(row => row.Name == "Drill").WithText);
        Assert.Equal("Drill is back in its place.", things.Message);

        things.Items.Single(row => row.Name == "Blender").EditCommand.Execute(null);
        Assert.True(things.IsFormOpen);
        Assert.Equal("Kitchen", things.Location);
        things.Name = "Stick blender";
        things.SaveCommand.Execute(null);
        Assert.Contains(things.Items, row => row.Name == "Stick blender");

        things.Items.Single(row => row.Name == "Stick blender").DeleteCommand.Execute(null);
        Assert.Equal(["Drill"], things.Items.Select(row => row.Name));
    }

    [Fact]
    public void A_warranty_about_to_end_shows_in_the_notifications_once()
    {
        var home = SetUp(Launch());
        home.Inventory.Name = "Fridge";
        home.Inventory.WarrantyEnds = new DateTime(2026, 11, 5);
        home.Inventory.SaveCommand.Execute(null);
        Assert.True(home.Inventory.Items.Single().IsWarrantyEnding);

        home.OpenNotificationsCommand.Execute(null);
        home.OpenNotificationsCommand.Execute(null);

        Assert.Single(home.Notifications!.Items, row => row.Title == "The warranty on Fridge ends in 15 days");
    }

    [Fact]
    public void A_scanned_label_opens_its_item_on_the_things_tab()
    {
        var home = SetUp(Launch());
        foreach (var name in new[] { "Drill", "Ladder" })
        {
            home.Inventory.Name = name;
            home.Inventory.SaveCommand.Execute(null);
        }

        var ladder = home.Inventory.Items.Single(row => row.Name == "Ladder").Item;
        Assert.Equal(ladder.Id, ItemLinks.Parse(ladder.Link));
        Assert.Equal(ladder.Id, ItemLinks.Parse("etchasketch://item/" + ladder.Id.ToString().ToUpperInvariant() + "/"));
        Assert.Null(ItemLinks.Parse("https://example.com/item/" + ladder.Id));
        Assert.Null(ItemLinks.Parse("etchasketch://item/not-an-id"));

        ItemLinks.RequestOpen(ladder.Id);
        home.OpenRequestedItems();

        Assert.Equal(HomeViewModel.InventoryTab, home.SelectedTab);
        Assert.True(home.Inventory.IsShowingOne);
        Assert.Equal(["Ladder"], home.Inventory.Items.Select(row => row.Name));
        Assert.Empty(ItemLinks.TakeOpened());

        home.Inventory.ShowEverythingCommand.Execute(null);
        Assert.Equal(2, home.Inventory.Items.Count);

        ItemLinks.RequestOpen(Guid.NewGuid());
        home.OpenRequestedItems();
        Assert.False(home.Inventory.IsShowingOne);
        Assert.Equal("That label is for something that isn't in this household's inventory.", home.Inventory.Message);
    }

    [Fact]
    public void A_label_carries_only_the_items_link_and_its_name()
    {
        var item = new Core.Pantry.InventoryItem
        {
            HouseholdId = Guid.NewGuid(),
            Name = "Mum's <good> drill",
            Serial = "SECRET-SERIAL",
            Notes = "spare key inside",
            Price = 199,
        };

        var svg = ItemLabel.Svg(item);
        var document = System.Xml.Linq.XDocument.Parse(svg);

        Assert.Equal("Mum's <good> drill", document.Descendants().Single(element => element.Name.LocalName == "text").Value);
        Assert.DoesNotContain("SECRET-SERIAL", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("spare key", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("199", svg, StringComparison.Ordinal);
        Assert.Equal("label-mum-s--good--drill.svg", ItemLabel.FileName(item));

        // The code itself: the same matrix QRCoder makes for the link, drawn one square per dark module.
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(item.Link, QRCoder.QRCodeGenerator.ECCLevel.M);
        var dark = data.ModuleMatrix.Sum(row => row.Cast<bool>().Count(module => module));
        var path = document.Descendants().Single(element => element.Name.LocalName == "path").Attribute("d")!.Value;
        Assert.Equal(dark, path.Count(c => c == 'M'));
    }

    [Fact]
    public void A_vehicles_service_comes_due_after_a_fill_up_and_moves_on_when_done()
    {
        var home = SetUp(Launch());
        var things = home.Inventory;
        things.Name = "Corolla";
        things.IsVehicle = true;
        things.SaveCommand.Execute(null);
        var car = things.Items.Single();
        Assert.StartsWith("Vehicle", car.Details, StringComparison.Ordinal);

        car.DetailCommand.Execute(null);
        var detail = things.Detail!;
        Assert.True(detail.IsVehicle);
        detail.EntryKind = detail.LogKinds.Single(kind => kind.Label == "Fuel");
        detail.EntryDistance = 30_000;
        detail.EntryAmount = 40;
        detail.EntryCost = 70;
        detail.AddEntryCommand.Execute(null);
        Assert.Equal("At 30,000 km · spent 70.00 in total", detail.Summary);

        detail.UpkeepName = "Oil change";
        detail.EveryDistance = 10_000;
        detail.AddUpkeepCommand.Execute(null);
        Assert.Null(detail.Message);
        var oil = Assert.Single(detail.Upkeep);
        Assert.Equal("every 10,000 km · next 40,000 km", oil.Details);
        Assert.False(oil.IsDue);

        detail.EntryKind = detail.LogKinds.Single(kind => kind.Label == "Fuel");
        detail.EntryDistance = 40_200;
        detail.AddEntryCommand.Execute(null);
        Assert.True(detail.Upkeep.Single().IsDue);
        home.OpenNotificationsCommand.Execute(null);
        Assert.Contains(home.Notifications!.Items, row => row.Title == "Corolla: Oil change is due");

        detail.Upkeep.Single().DoneTodayCommand.Execute(null);
        Assert.Equal("every 10,000 km · next 50,200 km · done once", detail.Upkeep.Single().Details);
        Assert.Equal("21 Oct 2026 · Oil change · 40,200 km", detail.Log[0].Text);

        detail.CloseCommand.Execute(null);
        Assert.False(things.IsShowingDetail);
    }

    [Fact]
    public void A_battery_is_charged_on_a_schedule_and_counts_its_charges()
    {
        var home = SetUp(Launch());
        home.Inventory.Name = "Drill battery";
        home.Inventory.SaveCommand.Execute(null);
        home.Inventory.Items.Single().DetailCommand.Execute(null);
        var detail = home.Inventory.Detail!;
        Assert.False(detail.IsVehicle);

        detail.UpkeepName = "Charge";
        detail.EveryDistance = 500;
        detail.EveryDays = 90;
        detail.AddUpkeepCommand.Execute(null);
        Assert.Equal("every 90 days · next 19 Jan 2027", detail.Upkeep.Single().Details);

        detail.Upkeep.Single().DoneTodayCommand.Execute(null);
        detail.Upkeep.Single().DoneTodayCommand.Execute(null);
        Assert.EndsWith("done 2 times", detail.Upkeep.Single().Details, StringComparison.Ordinal);
    }

    private sealed class FakeProducts : Core.Pantry.IProductLookup
    {
        public bool Offline { get; set; }

        public List<string> Asked { get; } = [];

        public Task<Core.Pantry.ProductInfo?> LookupAsync(string barcode, CancellationToken cancellationToken = default)
        {
            Asked.Add(barcode);
            return Offline
                ? throw new HttpRequestException("No network")
                : Task.FromResult(barcode == "4006381333931" ? new Core.Pantry.ProductInfo("Oat drink", "Oatly", "1 l") : null);
        }
    }

    private sealed class FakeScanner(string? result) : IBarcodeScanner
    {
        public Task<string?> ScanAsync() => result is null ? throw new BarcodeScannerMissingException() : Task.FromResult<string?>(result);
    }

    [Fact]
    public async Task A_barcode_fills_the_name_and_finds_it_again_next_time()
    {
        var products = new FakeProducts();
        var pantry = SetUp(Launch(products: products)).Pantry;
        Assert.False(pantry.CanScan);

        pantry.Barcode = "4 006381 333931";
        await pantry.LookUpCommand.ExecuteAsync(null);
        Assert.Equal(("Oatly Oat drink", "4006381333931"), (pantry.Name, pantry.Barcode));
        Assert.Equal("Found Oatly Oat drink (1 l).", pantry.Message);
        pantry.SaveCommand.Execute(null);

        pantry.Barcode = "4006381333931";
        await pantry.LookUpCommand.ExecuteAsync(null);
        Assert.True(pantry.IsEditing);
        Assert.Equal("Already in stock: Oatly Oat drink. Change the amount and save.", pantry.Message);
        Assert.Single(products.Asked);
    }

    [Fact]
    public async Task With_no_network_the_barcode_stays_and_the_name_can_be_typed()
    {
        var pantry = SetUp(Launch(products: new FakeProducts { Offline = true })).Pantry;

        pantry.Barcode = "73513537";
        await pantry.LookUpCommand.ExecuteAsync(null);
        Assert.Equal("Couldn't reach Open Food Facts. Type the name instead; the barcode is kept.", pantry.Message);

        pantry.Name = "Crackers";
        pantry.SaveCommand.Execute(null);
        Assert.Equal("Crackers", pantry.Items.Single().Name);

        pantry.Barcode = "73513537";
        await pantry.LookUpCommand.ExecuteAsync(null);
        Assert.Equal("Crackers", pantry.Name);
    }

    [Fact]
    public async Task A_wrong_barcode_is_explained_and_not_saved()
    {
        var products = new FakeProducts();
        var pantry = SetUp(Launch(products: products)).Pantry;

        pantry.Barcode = "4006381333932";
        await pantry.LookUpCommand.ExecuteAsync(null);
        Assert.StartsWith("That doesn't look like a barcode.", pantry.Message, StringComparison.Ordinal);
        Assert.Empty(products.Asked);

        pantry.Name = "Milk";
        pantry.SaveCommand.Execute(null);
        Assert.StartsWith("That barcode doesn't check out.", pantry.Message, StringComparison.Ordinal);
        Assert.Empty(pantry.Items);
    }

    [Fact]
    public async Task Scanning_looks_the_barcode_up_and_a_missing_scanner_app_is_explained()
    {
        var pantry = SetUp(Launch(scanner: new FakeScanner("4006381333931"))).Pantry;
        Assert.True(pantry.CanScan);
        await pantry.ScanCommand.ExecuteAsync(null);
        Assert.Equal("Oatly Oat drink", pantry.Name);

        var other = new ScreenTests();
        try
        {
            var noApp = other.SetUp(other.Launch(scanner: new FakeScanner(null))).Pantry;
            await noApp.ScanCommand.ExecuteAsync(null);
            Assert.StartsWith("Scanning needs a barcode scanner app.", noApp.Message, StringComparison.Ordinal);
        }
        finally
        {
            other.Dispose();
        }
    }

    [Fact]
    public void Things_and_a_vehicles_records_are_imported_from_the_things_tab()
    {
        var home = SetUp(Launch());
        var things = home.Inventory;
        things.OpenImportCommand.Execute(null);
        var import = things.Importer!;

        import.FileText = "HB.name,HB.location,HB.asset_id,HB.archived\nLadder,Home / Shed,000-001,false\nOld TV,Home,000-002,true\n";
        import.PreviewCommand.Execute(null);
        Assert.Equal("Ready to add 1 thing, 1 left out.", import.Summary);
        Assert.Equal("Row 3: Old TV is archived in Homebox", import.Problems);
        Assert.Empty(things.Items);
        import.ImportCommand.Execute(null);
        Assert.Equal("Added 1 thing, 1 left out.", import.Summary);
        Assert.False(import.CanImport);
        Assert.Equal(["Ladder"], things.Items.Select(row => row.Name));

        import.Source = import.Sources.Single(source => source.Source == ThingsSource.LubeLogger);
        Assert.True(import.HasNoVehicles);
        import.FileText = "Date,Odometer,FuelConsumed,Cost\n2026-10-20,40100,40,70\n";
        import.PreviewCommand.Execute(null);
        Assert.StartsWith("Add the vehicle on the Things tab first", import.ErrorMessage, StringComparison.Ordinal);
        import.CloseCommand.Execute(null);
        Assert.False(things.IsImporting);

        things.Name = "Corolla";
        things.IsVehicle = true;
        things.SaveCommand.Execute(null);
        things.OpenImportCommand.Execute(null);
        import = things.Importer!;
        import.Source = import.Sources.Single(source => source.Source == ThingsSource.LubeLogger);
        Assert.Equal("Corolla", import.Vehicle!.Label);
        import.FileText = "Date,Odometer,FuelConsumed,Cost\n2026-10-20,40100,40,70\n";
        import.PreviewCommand.Execute(null);
        import.ImportCommand.Execute(null);
        Assert.Equal("Added 1 log entry.", import.Summary);

        things.Items.Single(row => row.Name == "Corolla").DetailCommand.Execute(null);
        Assert.Equal("At 40,100 km · spent 70.00 in total", things.Detail!.Summary);
    }

    [Fact]
    public async Task Grocy_is_checked_then_imported_from_the_import_panel_and_the_key_is_needed()
    {
        var asked = new List<(Uri, string)>();
        Func<string, Task<System.Text.Json.JsonElement>> Reader(Uri address, string key)
        {
            asked.Add((address, key));
            return path => Task.FromResult(System.Text.Json.JsonDocument.Parse(path switch
            {
                "api/objects/chores" => """[{"id":1,"name":"Water plants","period_type":"daily","period_interval":3,"active":1}]""",
                "api/chores" => """[{"chore_id":1,"next_estimated_execution_time":"2026-10-22 00:00:00"}]""",
                _ => "[]",
            }).RootElement.Clone());
        }

        var home = SetUp(Launch(grocy: Reader));
        home.OpenImportCommand.Execute(null);
        var grocy = home.Import!.Grocy;

        grocy.Address = "grocy.example.com";
        await grocy.CheckCommand.ExecuteAsync(null);
        Assert.StartsWith("Type Grocy's address", grocy.ErrorMessage, StringComparison.Ordinal);
        grocy.Address = "https://grocy.example.com";
        await grocy.CheckCommand.ExecuteAsync(null);
        Assert.StartsWith("Paste an API key", grocy.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(asked);

        grocy.ApiKey = " key-123 ";
        await grocy.CheckCommand.ExecuteAsync(null);
        Assert.Equal((new Uri("https://grocy.example.com/"), "key-123"), asked.Single());
        Assert.Equal("Ready to add 1 chore, 0 pantry items, 0 batteries, 0 recipes. Chores have no due date yet: each is first due a repeat after it's next done.", grocy.Summary);
        Assert.Empty(home.ChoreColumns.SelectMany(column => column.Chores));

        grocy.KeepDueDates = true;
        Assert.EndsWith("Chores are due when Grocy says, so reminders start.", grocy.Summary, StringComparison.Ordinal);
        grocy.ImportCommand.Execute(null);
        Assert.StartsWith("Added 1 chore", grocy.Summary, StringComparison.Ordinal);
        Assert.False(grocy.CanImport);

        home.Import.CloseCommand.Execute(null);
        var chore = Assert.Single(home.ChoreColumns.SelectMany(column => column.Chores));
        Assert.Equal("Water plants", chore.Title);
    }

    [Fact]
    public void Everything_export_brings_the_household_modules_back_on_a_fresh_install()
    {
        var home = SetUp(Launch());
        home.Pantry.Name = "Milk";
        home.Pantry.SaveCommand.Execute(null);
        home.Inventory.Name = "Ladder";
        home.Inventory.SaveCommand.Execute(null);
        AddTask(home, "Clean gutters");
        home.OpenExportCommand.Execute(null);
        var file = home.Export!.Build(ExportKind.Everything);

        var other = new ScreenTests();
        try
        {
            var fresh = other.SetUp(other.Launch());
            fresh.OpenImportCommand.Execute(null);
            fresh.Import!.FileText = file;
            fresh.Import.PreviewCommand.Execute(null);
            Assert.Equal("Ready to add 1 task. Household: 1 pantry item, 1 thing.", fresh.Import.Summary);
            fresh.Import.ImportCommand.Execute(null);
            fresh.Import.CloseCommand.Execute(null);

            Assert.Equal(["Milk"], fresh.Pantry.Items.Select(row => row.Name));
            Assert.Equal(["Ladder"], fresh.Inventory.Items.Select(row => row.Name));
            Assert.Contains(fresh.MyTasks, row => row.Title == "Clean gutters");

            fresh.OpenImportCommand.Execute(null);
            fresh.Import!.FileText = file;
            fresh.Import.PreviewCommand.Execute(null);
            Assert.Equal("Ready to add 0 tasks, 1 already here. Household: nothing new (2 already here).", fresh.Import.Summary);
            Assert.False(fresh.Import.CanImport);
        }
        finally
        {
            other.Dispose();
        }
    }
}
