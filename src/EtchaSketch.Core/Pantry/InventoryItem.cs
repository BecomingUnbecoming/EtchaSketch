using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Pantry;

/// <summary>
/// Something the household owns: where it is, when it was bought, until when it's under warranty,
/// and who has borrowed it. Shared with the household.
/// </summary>
public sealed record InventoryItem : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required string Name { get; init; }

    /// <summary>Where it lives, like "Garage shelf 2".</summary>
    [Tier(PrivacyTier.Household)]
    public string? Location { get; init; }

    /// <summary>Like "Tools", "Kitchen" or "Electronics".</summary>
    [Tier(PrivacyTier.Household)]
    public string? Category { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Serial { get; init; }

    [Tier(PrivacyTier.Household)]
    public DateOnly? PurchasedOn { get; init; }

    [Tier(PrivacyTier.Household)]
    public decimal? Price { get; init; }

    [Tier(PrivacyTier.Household)]
    public DateOnly? WarrantyEnds { get; init; }

    /// <summary>A link to the manual, or where the paper one is.</summary>
    [Tier(PrivacyTier.Household)]
    public string? Manual { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Notes { get; init; }

    /// <summary>Who has it right now, if someone took it out of its place.</summary>
    [Tier(PrivacyTier.Household)]
    public Guid? BorrowedBy { get; init; }

    /// <summary>"km" or "mi" for a vehicle, whose log records the distance on its clock; null for anything else.</summary>
    [Tier(PrivacyTier.Household)]
    public string? DistanceUnit { get; init; }

    public bool IsVehicle => DistanceUnit is not null;

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;

    /// <summary>What a QR label on it points to. Scanning it on a phone opens the item in the app.</summary>
    public string Link => $"etchasketch://item/{Id}";
}
