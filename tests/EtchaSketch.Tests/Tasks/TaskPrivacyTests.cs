using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Tasks;

namespace EtchaSketch.Tests.Tasks;

public class TaskPrivacyTests
{
    private static readonly Guid Lacy = Guid.NewGuid();
    private static readonly Guid Jordy = Guid.NewGuid();

    private static TaskItem Task(PrivacyTier tier) => new()
    {
        HouseholdId = Guid.NewGuid(),
        OwnerId = Lacy,
        Title = "Paint the fence",
        Notes = "use the leftover blue",
        Due = new DateOnly(2026, 10, 30),
        Recurrence = new Recurrence(RecurrenceFrequency.Weekly),
        Tier = tier,
    };

    [Fact]
    public void New_tasks_are_private_by_default()
    {
        Assert.Equal(PrivacyTier.Private, new TaskItem { HouseholdId = Guid.Empty, OwnerId = Lacy, Title = "x" }.Tier);
    }

    [Fact]
    public void Notes_stay_with_the_owner()
    {
        Assert.Contains("Notes", PrivacyGateway.Project(Task(PrivacyTier.Household), Audience.ForProfile(Lacy))!.Keys);
        Assert.DoesNotContain("Notes", PrivacyGateway.Project(Task(PrivacyTier.Household), Audience.ForProfile(Jordy))!.Keys);
    }

    [Fact]
    public void Household_members_see_a_shared_chore_and_its_schedule()
    {
        var view = PrivacyGateway.Project(Task(PrivacyTier.Household), Audience.ForProfile(Jordy))!;

        Assert.Contains("Title", view.Keys);
        Assert.Contains("Due", view.Keys);
        Assert.IsType<GatewayView>(view["Recurrence"]);
    }

    [Fact]
    public void Only_a_public_task_shows_on_stream_and_then_only_its_title()
    {
        Assert.Null(PrivacyGateway.ForStudio(Task(PrivacyTier.Household)));
        Assert.Equal(["Title"], PrivacyGateway.ForStudio(Task(PrivacyTier.Public))!.Keys);
    }
}
