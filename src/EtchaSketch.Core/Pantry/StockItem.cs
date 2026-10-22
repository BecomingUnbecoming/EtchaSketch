using EtchaSketch.Core.Privacy;

namespace EtchaSketch.Core.Pantry;

/// <summary>
/// Something the household keeps in stock: food in the pantry, toilet roll, batteries. It knows how
/// much there is, when it goes off, and how low it can run before it goes on the shopping list.
/// Shared with the household by default.
/// </summary>
public sealed record StockItem : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required string Name { get; init; }

    [Tier(PrivacyTier.Household)]
    public decimal Amount { get; init; }

    /// <summary>Like "g", "packs" or "bottles". Null counts items.</summary>
    [Tier(PrivacyTier.Household)]
    public string? Unit { get; init; }

    /// <summary>Where it's kept, like "Fridge" or "Hall cupboard".</summary>
    [Tier(PrivacyTier.Household)]
    public string? Location { get; init; }

    /// <summary>The earliest best-before date of what's in stock.</summary>
    [Tier(PrivacyTier.Household)]
    public DateOnly? BestBefore { get; init; }

    /// <summary>At or below this, it counts as running low and can go on the shopping list. Null never runs low.</summary>
    [Tier(PrivacyTier.Household)]
    public decimal? LowAt { get; init; }

    /// <summary>The product's barcode, so a scan finds it again.</summary>
    [Tier(PrivacyTier.Household)]
    public string? Barcode { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;

    public bool IsLow => LowAt is { } low && Amount <= low;
}

/// <summary>One line on the shared shopping list. Ticking one that came from stock puts it back in stock.</summary>
public sealed record ShoppingItem : ITiered
{
    [Tier(PrivacyTier.Household)]
    public Guid Id { get; init; } = Guid.NewGuid();

    [Tier(PrivacyTier.Household)]
    public required Guid HouseholdId { get; init; }

    [Tier(PrivacyTier.Household)]
    public required string Name { get; init; }

    /// <summary>How much to buy, if it matters.</summary>
    [Tier(PrivacyTier.Household)]
    public decimal? Amount { get; init; }

    [Tier(PrivacyTier.Household)]
    public string? Unit { get; init; }

    /// <summary>The stock item it tops up, if any.</summary>
    [Tier(PrivacyTier.Household)]
    public Guid? StockItemId { get; init; }

    /// <summary>When it was ticked off in the shop, or null while it's still to get.</summary>
    [Tier(PrivacyTier.Household)]
    public DateTimeOffset? BoughtAt { get; init; }

    public PrivacyTier Tier { get; init; } = PrivacyTier.Household;
}
