using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Tests.Privacy;

/// <summary>Private means private to the person who owns the item, not to everyone in the household.</summary>
public class OwnershipTests
{
    private static readonly Guid Robin = Guid.NewGuid();
    private static readonly Guid Kai = Guid.NewGuid();

    private sealed record Journal(
        [property: Tier(PrivacyTier.Household)] string Mood,
        [property: Tier(PrivacyTier.Private)] string Entry,
        [property: Tier(PrivacyTier.Secret)] string Recovery) : ITiered, IOwned
    {
        public PrivacyTier Tier { get; init; } = PrivacyTier.Household;
        public required Guid OwnerId { get; init; }
    }

    private sealed record Chore([property: Tier(PrivacyTier.Household)] string Name) : ITiered
    {
        public PrivacyTier Tier { get; init; } = PrivacyTier.Household;
    }

    private static Journal RobinsJournal(PrivacyTier tier = PrivacyTier.Household) =>
        new("tired", "long day", "word list") { OwnerId = Robin, Tier = tier };

    [Fact]
    public void Owner_sees_their_own_private_fields()
    {
        var view = PrivacyGateway.Project(RobinsJournal(), Audience.ForProfile(Robin))!;

        Assert.Equal(["Mood", "Entry"], view.Keys);
    }

    [Fact]
    public void Another_household_member_sees_only_household_fields()
    {
        var view = PrivacyGateway.Project(RobinsJournal(), Audience.ForProfile(Kai))!;

        Assert.Equal(["Mood"], view.Keys);
    }

    [Fact]
    public void Another_member_never_sees_a_private_item_even_when_unlocked()
    {
        Assert.Null(PrivacyGateway.Project(RobinsJournal(PrivacyTier.Private), Audience.ForProfile(Kai, unlocked: true)));
        Assert.NotNull(PrivacyGateway.Project(RobinsJournal(PrivacyTier.Private), Audience.ForProfile(Robin)));
    }

    [Fact]
    public void Secret_fields_need_the_owner_and_an_unlock()
    {
        Assert.DoesNotContain("Recovery", PrivacyGateway.Project(RobinsJournal(), Audience.ForProfile(Robin))!.Keys);
        Assert.Contains("Recovery", PrivacyGateway.Project(RobinsJournal(), Audience.ForProfile(Robin, unlocked: true))!.Keys);
        Assert.DoesNotContain("Recovery", PrivacyGateway.Project(RobinsJournal(), Audience.ForProfile(Kai, unlocked: true))!.Keys);
    }

    [Fact]
    public void An_audience_with_no_profile_is_never_treated_as_the_owner()
    {
        var view = PrivacyGateway.Project(RobinsJournal(), new Audience(PrivacyTier.Secret))!;

        Assert.Equal(["Mood"], view.Keys);
    }

    [Fact]
    public void Shared_household_items_are_visible_to_every_member()
    {
        Assert.Equal(["Name"], PrivacyGateway.Project(new Chore("Dishes"), Audience.ForProfile(Kai))!.Keys);
        Assert.Null(PrivacyGateway.Project(new Chore("Dishes"), Audience.Stream));
    }
}
