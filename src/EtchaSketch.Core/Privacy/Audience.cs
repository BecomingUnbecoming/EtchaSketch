namespace EtchaSketch.Core.Privacy;

/// <summary>
/// Who is looking: the most restricted tier they are cleared to see, and which profile they are.
/// </summary>
/// <param name="ViewerId">The viewer's profile, or null for viewers who aren't a household member.</param>
public sealed record Audience(PrivacyTier Clearance, Guid? ViewerId = null)
{
    /// <summary>Stream viewers and the studio window.</summary>
    public static Audience Stream { get; } = new(PrivacyTier.Public);

    /// <summary>Friends in a shared circle.</summary>
    public static Audience Circle { get; } = new(PrivacyTier.Circle);

    /// <summary>Household members as a group, such as a shared screen in the kitchen.</summary>
    public static Audience Household { get; } = new(PrivacyTier.Household);

    /// <summary>One household member looking at the app as themselves.</summary>
    /// <param name="unlocked">True once they've unlocked their Secret items.</param>
    public static Audience ForProfile(Guid profileId, bool unlocked = false) =>
        new(unlocked ? PrivacyTier.Secret : PrivacyTier.Private, profileId);

    /// <summary>
    /// The clearance this audience has for one item. Someone else's item is capped at Household,
    /// so its owner's Private and Secret parts stay theirs alone.
    /// </summary>
    public PrivacyTier ClearanceFor(ITiered item) =>
        item is IOwned owned && owned.OwnerId != ViewerId && Clearance > PrivacyTier.Household
            ? PrivacyTier.Household
            : Clearance;
}
