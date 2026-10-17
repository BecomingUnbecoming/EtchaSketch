using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Tests.Privacy;

public class PrivacyGatewayTests
{
    // A stand-in for a real item: a Public task with one field of each tier and one untagged field.
    private sealed record SampleTask(
        [property: Tier(PrivacyTier.Public)] string Title,
        [property: Tier(PrivacyTier.Circle)] string SharedWith,
        [property: Tier(PrivacyTier.Household)] string Room,
        [property: Tier(PrivacyTier.Private)] string Notes,
        [property: Tier(PrivacyTier.Secret)] string DoorCode,
        string UntaggedJournal) : ITiered
    {
        public PrivacyTier Tier { get; init; } = PrivacyTier.Public;
    }

    private sealed record Project(
        [property: Tier(PrivacyTier.Public)] string Name,
        [property: Tier(PrivacyTier.Public)] IReadOnlyList<SampleTask> Tasks,
        [property: Tier(PrivacyTier.Public)] SampleTask? Featured) : ITiered
    {
        public PrivacyTier Tier { get; init; } = PrivacyTier.Public;
    }

    private static readonly Guid Me = Guid.NewGuid();

    private static SampleTask Task(PrivacyTier tier = PrivacyTier.Public) =>
        new("Paint the fence", "Brooks", "Garage", "felt anxious", "4821", "dear diary") { Tier = tier };

    [Fact]
    public void Untagged_field_never_reaches_a_public_view()
    {
        var view = PrivacyGateway.Project(Task(), Audience.Stream)!;

        Assert.False(view.ContainsKey(nameof(SampleTask.UntaggedJournal)));
        Assert.DoesNotContain("dear diary", view.Values);
    }

    [Fact]
    public void Untagged_field_is_treated_as_private()
    {
        Assert.False(PrivacyGateway.Project(Task(), Audience.Household)!.ContainsKey(nameof(SampleTask.UntaggedJournal)));
        Assert.True(PrivacyGateway.Project(Task(), Audience.ForProfile(Me))!.ContainsKey(nameof(SampleTask.UntaggedJournal)));
    }

    [Fact]
    public void Public_view_contains_only_public_fields()
    {
        var view = PrivacyGateway.Project(Task(), Audience.Stream)!;

        Assert.Equal([nameof(SampleTask.Title)], view.Keys);
        Assert.Equal("Paint the fence", view[nameof(SampleTask.Title)]);
    }

    [Theory]
    [InlineData(PrivacyTier.Public, new[] { "Title" })]
    [InlineData(PrivacyTier.Circle, new[] { "Title", "SharedWith" })]
    [InlineData(PrivacyTier.Household, new[] { "Title", "SharedWith", "Room" })]
    [InlineData(PrivacyTier.Private, new[] { "Title", "SharedWith", "Room", "Notes", "UntaggedJournal" })]
    [InlineData(PrivacyTier.Secret, new[] { "Title", "SharedWith", "Room", "Notes", "DoorCode", "UntaggedJournal" })]
    public void Each_audience_sees_its_tier_and_everything_more_open(PrivacyTier clearance, string[] expected)
    {
        var view = PrivacyGateway.Project(Task(), new Audience(clearance))!;

        Assert.Equal(expected.Order(), view.Keys.Order());
    }

    [Fact]
    public void Item_above_the_audience_is_withheld_entirely()
    {
        Assert.Null(PrivacyGateway.Project(Task(PrivacyTier.Private), Audience.Stream));
        Assert.Null(PrivacyGateway.Project(Task(PrivacyTier.Household), Audience.Circle));
    }

    [Fact]
    public void A_public_field_cannot_loosen_its_item()
    {
        // Title is tagged Public, but the task itself is Household, so a Circle viewer sees nothing.
        Assert.Null(PrivacyGateway.Project(Task(PrivacyTier.Household), Audience.Circle));
        Assert.True(PrivacyGateway.Project(Task(PrivacyTier.Household), Audience.Household)!.ContainsKey("Title"));
    }

    [Fact]
    public void Secret_is_hidden_from_the_owner_until_unlocked()
    {
        Assert.False(PrivacyGateway.Project(Task(), Audience.ForProfile(Me))!.ContainsKey(nameof(SampleTask.DoorCode)));
        Assert.True(PrivacyGateway.Project(Task(), Audience.ForProfile(Me, unlocked: true))!.ContainsKey(nameof(SampleTask.DoorCode)));
    }

    [Fact]
    public void Nested_items_are_projected_too()
    {
        var project = new Project("Yard", [Task(), Task(PrivacyTier.Private)], Task(PrivacyTier.Private));

        var view = PrivacyGateway.Project(project, Audience.Stream)!;

        var tasks = Assert.IsAssignableFrom<IReadOnlyList<GatewayView>>(view[nameof(Project.Tasks)]);
        var only = Assert.Single(tasks);
        Assert.Equal([nameof(SampleTask.Title)], only.Keys);
        Assert.False(view.ContainsKey(nameof(Project.Featured)));
    }

    [Fact]
    public void Nothing_private_appears_anywhere_in_a_public_view()
    {
        var project = new Project("Yard", [Task(), Task(PrivacyTier.Secret)], Task());
        var privateValues = new[] { "Brooks", "Garage", "felt anxious", "4821", "dear diary" };

        var flattened = Flatten(PrivacyGateway.Project(project, Audience.Stream)!).ToList();

        Assert.DoesNotContain(flattened, value => privateValues.Contains(value));
        Assert.DoesNotContain(flattened, value => value is ITiered);
    }

    private sealed record Address(string Street);

    private sealed record Contact(
        [property: Tier(PrivacyTier.Public)] string Name,
        [property: Tier(PrivacyTier.Public)] Address Home) : ITiered
    {
        public PrivacyTier Tier { get; init; } = PrivacyTier.Public;
    }

    [Fact]
    public void Nested_object_without_tiers_is_withheld_even_when_its_field_is_public()
    {
        var view = PrivacyGateway.Project(new Contact("Brooks", new Address("12 Elm St")), Audience.Stream)!;

        Assert.Equal([nameof(Contact.Name)], view.Keys);
    }

    [Fact]
    public void Default_tier_is_private()
    {
        Assert.Equal(PrivacyTier.Private, default(PrivacyTier));
    }

    [Fact]
    public void Tiers_run_from_most_open_to_most_restricted()
    {
        Assert.True(PrivacyTier.Public < PrivacyTier.Circle);
        Assert.True(PrivacyTier.Circle < PrivacyTier.Household);
        Assert.True(PrivacyTier.Household < PrivacyTier.Private);
        Assert.True(PrivacyTier.Private < PrivacyTier.Secret);
    }

    [Fact]
    public void Studio_only_ever_receives_public()
    {
        Assert.Equal(PrivacyTier.Public, Audience.Stream.Clearance);
        Assert.Equal(PrivacyGateway.Project(Task(), Audience.Stream)!.Keys, PrivacyGateway.ForStudio(Task())!.Keys);
    }

    private static IEnumerable<object?> Flatten(object? value) => value switch
    {
        GatewayView view => view.Values.SelectMany(Flatten),
        IEnumerable<GatewayView> views => views.SelectMany(Flatten),
        _ => [value],
    };
}
