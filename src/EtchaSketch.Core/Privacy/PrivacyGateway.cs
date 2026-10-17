using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace EtchaSketch.Core.Privacy;

/// <summary>
/// The one place data passes from the app's store to anything that shows it to other people.
/// It copies out only what the audience may see, so a view can't leak what it never received.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>An item above the audience's clearance is withheld entirely.</item>
/// <item>A property's tier is the more restricted of its <see cref="TierAttribute"/> (Private if untagged) and its item's tier.</item>
/// <item>Nested <see cref="ITiered"/> items, alone or in lists, go through the same rules.</item>
/// <item>Any other nested object is withheld, because its own properties carry no tiers.</item>
/// </list>
/// </remarks>
public static class PrivacyGateway
{
    private static readonly ConcurrentDictionary<Type, TieredProperty[]> PropertiesByType = new();

    /// <summary>Projects an item for an audience, or returns null when the audience may not see it at all.</summary>
    public static GatewayView? Project(ITiered item, Audience audience)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(audience);
        return Project(item, audience, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>Projects an item for the studio window, which only ever receives Public data.</summary>
    public static GatewayView? ForStudio(ITiered item) => Project(item, Audience.Stream);

    private static GatewayView? Project(ITiered item, Audience audience, HashSet<object> path)
    {
        if (!audience.CanSee(item.Tier) || !path.Add(item))
        {
            return null;
        }

        var fields = new List<KeyValuePair<string, object?>>();
        foreach (var property in PropertiesOf(item.GetType()))
        {
            var tier = Max(property.Tier, item.Tier);
            if (!audience.CanSee(tier))
            {
                continue;
            }

            if (TryCopy(property.Info.GetValue(item), audience, path, out var value))
            {
                fields.Add(new(property.Info.Name, value));
            }
        }

        path.Remove(item);
        return new GatewayView(item.Tier, fields);
    }

    /// <summary>Copies a property value into something safe to hand out, or returns false to withhold it.</summary>
    private static bool TryCopy(object? value, Audience audience, HashSet<object> path, out object? copy)
    {
        switch (value)
        {
            case null:
                copy = null;
                return true;
            case ITiered nested:
                copy = Project(nested, audience, path);
                return copy is not null;
            case string or Enum:
            case var _ when IsSimple(value.GetType()):
                copy = value;
                return true;
            case IEnumerable items:
                return TryCopyList(items, audience, path, out copy);
            default:
                copy = null;
                return false;
        }
    }

    private static bool TryCopyList(IEnumerable items, Audience audience, HashSet<object> path, out object? copy)
    {
        var all = items.Cast<object?>().ToList();
        if (all.All(item => item is ITiered))
        {
            copy = all.Cast<ITiered>()
                .Select(item => Project(item, audience, path))
                .OfType<GatewayView>()
                .ToList();
            return true;
        }

        if (all.All(item => item is null or string or Enum || IsSimple(item.GetType())))
        {
            copy = all;
            return true;
        }

        copy = null;
        return false;
    }

    private static bool IsSimple(Type type) =>
        type.IsPrimitive
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(Guid);

    private static PrivacyTier Max(PrivacyTier a, PrivacyTier b) => a > b ? a : b;

    private static TieredProperty[] PropertiesOf(Type type) =>
        PropertiesByType.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(ITiered.Tier))
            .Select(p => new TieredProperty(p, p.GetCustomAttribute<TierAttribute>()?.Tier ?? PrivacyTier.Private))
            .ToArray());

    private sealed record TieredProperty(PropertyInfo Info, PrivacyTier Tier);
}
