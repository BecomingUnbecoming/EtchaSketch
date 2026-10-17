namespace EtchaSketch.Core.Privacy;

/// <summary>
/// Who may see a piece of data, from most open to most restricted. A viewer cleared for one tier
/// sees that tier and every tier more open than it.
/// </summary>
/// <remarks>
/// The values are chosen so that <see cref="Private"/> is 0, which makes it the default for any tier
/// that is never set. Forgetting to tag something therefore hides it rather than publishing it.
/// </remarks>
public enum PrivacyTier
{
    /// <summary>Anyone, including stream viewers and public library rooms.</summary>
    Public = -3,

    /// <summary>Friends you share with, such as a shared library room.</summary>
    Circle = -2,

    /// <summary>Everyone in your household.</summary>
    Household = -1,

    /// <summary>Only you. The default for anything untagged.</summary>
    Private = 0,

    /// <summary>Only you, and only after unlocking. Door codes, recovery keys and the like.</summary>
    Secret = 1,
}
