using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Profiles;

/// <summary>One person in a household. Everyone in the household can see who else is in it.</summary>
public sealed record Profile(
    [property: Tier(PrivacyTier.Household)] Guid Id,
    [property: Tier(PrivacyTier.Household)] Guid HouseholdId,
    [property: Tier(PrivacyTier.Household)] string DisplayName,
    [property: Tier(PrivacyTier.Household)] ProfileRole Role) : ITiered
{
    public PrivacyTier Tier => PrivacyTier.Household;
}
