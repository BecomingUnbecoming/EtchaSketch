using System.Collections;

namespace EtchaSketch.Core.Privacy;

/// <summary>
/// What the gateway lets through for one item: property names mapped to values the audience may see.
/// Hidden properties are absent, not blanked, so a view cannot reveal that they exist.
/// </summary>
/// <remarks>
/// Values are simple values (text, numbers, dates and so on), lists of simple values, nested
/// <see cref="GatewayView"/>s, or lists of them. The original item is never reachable from a view.
/// </remarks>
public sealed class GatewayView : IReadOnlyDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _fields;
    private readonly List<string> _order;

    internal GatewayView(PrivacyTier tier, List<KeyValuePair<string, object?>> fields)
    {
        Tier = tier;
        _fields = new Dictionary<string, object?>(fields, StringComparer.Ordinal);
        _order = fields.Select(field => field.Key).ToList();
    }

    /// <summary>The tier of the item this view came from.</summary>
    public PrivacyTier Tier { get; }

    public object? this[string key] => _fields[key];
    public IEnumerable<string> Keys => _order;
    public IEnumerable<object?> Values => _order.Select(key => _fields[key]);
    public int Count => _fields.Count;
    public bool ContainsKey(string key) => _fields.ContainsKey(key);
    public bool TryGetValue(string key, out object? value) => _fields.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        _order.Select(key => new KeyValuePair<string, object?>(key, _fields[key])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
