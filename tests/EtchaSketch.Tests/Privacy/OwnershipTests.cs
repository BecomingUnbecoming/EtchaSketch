using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Tests.Privacy;

/// <summary>Private means private to the person who owns the item, not to everyone in the household.</summary>
public class OwnershipTests
{
    private static readonly Guid Lacy = Guid.NewGuid();
    private static readonly Guid Jordy = Guid.NewGuid();

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

    private static Journal LacysJournal(PrivacyTier tier = PrivacyTier.Household) =>
        new("tired", "long day", "word list") { OwnerId = Lacy, Tier = tier };

    [Fact]
    public void Owner_sees_their_own_private_fields()
    {
        var view = PrivacyGateway.Project(LacysJournal(), Audience.ForProfile(Lacy))!;

        Assert.Equal(["Mood", "Entry"], view.Keys);
    }

    [Fact]
    public void Another_household_member_sees_only_household_fields()
    {
        var view = PrivacyGateway.Project(LacysJournal(), Audience.ForProfile(Jordy))!;

        Assert.Equal(["Mood"], view.Keys);
    }

    [Fact]
    public void Another_member_never_sees_a_private_item_even_when_unlocked()
    {
        Assert.Null(PrivacyGateway.Project(LacysJournal(PrivacyTier.Private), Audience.ForProfile(Jordy, unlocked: true)));
        Assert.NotNull(PrivacyGateway.Project(LacysJournal(PrivacyTier.Private), Audience.ForProfile(Lacy)));
    }

    [Fact]
    public void Secret_fields_need_the_owner_and_an_unlock()
    {
        Assert.DoesNotContain("Recovery", PrivacyGateway.Project(LacysJournal(), Audience.ForProfile(Lacy))!.Keys);
        Assert.Contains("Recovery", PrivacyGateway.Project(LacysJournal(), Audience.ForProfile(Lacy, unlocked: true))!.Keys);
        Assert.DoesNotContain("Recovery", PrivacyGateway.Project(LacysJournal(), Audience.ForProfile(Jordy, unlocked: true))!.Keys);
    }

    [Fact]
    public void An_audience_with_no_profile_is_never_treated_as_the_owner()
    {
        var view = PrivacyGateway.Project(LacysJournal(), new Audience(PrivacyTier.Secret))!;

        Assert.Equal(["Mood"], view.Keys);
    }

    [Fact]
    public void Shared_household_items_are_visible_to_every_member()
    {
        Assert.Equal(["Name"], PrivacyGateway.Project(new Chore("Dishes"), Audience.ForProfile(Jordy))!.Keys);
        Assert.Null(PrivacyGateway.Project(new Chore("Dishes"), Audience.Stream));
    }
}
