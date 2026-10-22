using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Pantry;

/// <summary>
/// Something an item needs every so often: an oil change every 10,000 km or a year, charging a
/// battery every 3 months, a new filter every 6 months. Due when either limit is reached.
/// </summary>
public sealed record Upkeep : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required Guid ItemId { get; init; }

    /// <summary>Like "Oil change" or "Charge".</summary>
    [Tier(PrivacyTier.Household)]
    public required string Name { get; init; }

    [Tier(PrivacyTier.Household)]
    public int? EveryDays { get; init; }

    /// <summary>In the vehicle's own unit (km or mi). Only for vehicles.</summary>
    [Tier(PrivacyTier.Household)]
    public int? EveryDistance { get; init; }

    [Tier(PrivacyTier.Household)]
    public required DateOnly LastDoneOn { get; init; }

    /// <summary>The vehicle's distance when it was last done.</summary>
    [Tier(PrivacyTier.Household)]
    public int? LastDoneDistance { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;

    public DateOnly? NextOn => EveryDays is { } days ? LastDoneOn.AddDays(days) : null;

    public int? NextAtDistance => EveryDistance is { } every ? (LastDoneDistance ?? 0) + every : null;

    /// <summary>Whether the date or the distance has been reached.</summary>
    public bool IsDue(DateOnly today, int? distanceNow) =>
        NextOn is { } next && today >= next
        || NextAtDistance is { } at && distanceNow is { } now && now >= at;
}

public enum LogKind
{
    Fuel,
    Service,
    Repair,
    Charge,
    Note,
}

/// <summary>
/// One entry in an item's log: a fill-up, a service, a repair, a battery charge. A vehicle's entries
/// carry the distance on its clock, which is how a service due by distance is noticed.
/// </summary>
public sealed record ItemLogEntry : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required Guid ItemId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required LogKind Kind { get; init; }

    [Tier(PrivacyTier.Household)]
    public required DateOnly On { get; init; }

    [Tier(PrivacyTier.Household)]
    public int? Distance { get; init; }

    /// <summary>Litres (or gallons) for fuel.</summary>
    [Tier(PrivacyTier.Household)]
    public decimal? Amount { get; init; }

    [Tier(PrivacyTier.Household)]
    public decimal? Cost { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Note { get; init; }

    /// <summary>The upkeep this entry did, which then counts as done on this day and distance.</summary>
    [Tier(PrivacyTier.Household)]
    public Guid? UpkeepId { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;
}
