using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Profiles;

/// <summary>The people who share a home, and the Household tier of data they all see.</summary>
public sealed record Household(
    [property: Tier(PrivacyTier.Household)] Guid Id,
    [property: Tier(PrivacyTier.Household)] string Name,
    [property: Tier(PrivacyTier.Household)] IReadOnlyList<Profile> Members) : ITiered
{
    public PrivacyTier Tier => PrivacyTier.Household;
}
