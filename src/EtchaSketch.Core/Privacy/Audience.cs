namespace EtchaSketch.Core.Privacy;

/// <summary>Who is looking, expressed as the most restricted tier they are cleared to see.</summary>
public sealed record Audience(PrivacyTier Clearance)
{
    /// <summary>Stream viewers and the studio window.</summary>
    public static Audience Stream { get; } = new(PrivacyTier.Public);

    /// <summary>Friends in a shared circle.</summary>
    public static Audience Circle { get; } = new(PrivacyTier.Circle);

    /// <summary>Household members.</summary>
    public static Audience Household { get; } = new(PrivacyTier.Household);

    /// <summary>You, with the app unlocked but Secret items still closed.</summary>
    public static Audience Owner { get; } = new(PrivacyTier.Private);

    /// <summary>You, after unlocking Secret items.</summary>
    public static Audience OwnerUnlocked { get; } = new(PrivacyTier.Secret);

    public bool CanSee(PrivacyTier tier) => tier <= Clearance;
}
