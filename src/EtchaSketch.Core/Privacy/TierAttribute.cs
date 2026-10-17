namespace EtchaSketch.Core.Privacy;

/// <summary>
/// Tags a property with the tier it may be shown at. A property without this attribute is
/// <see cref="PrivacyTier.Private"/>.
/// </summary>
/// <remarks>
/// On a positional record, target the property: <c>record Task([property: Tier(PrivacyTier.Public)] string Title)</c>.
/// Without <c>property:</c> the attribute lands on the constructor parameter and the gateway won't see it.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class TierAttribute(PrivacyTier tier) : Attribute
{
    public PrivacyTier Tier { get; } = tier;
}
