using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EtchaSketch.UI.Services;

/// <summary>
/// An item's QR label scanned on a phone ("etchasketch://item/{id}"). The platform adds it here;
/// the home screen opens the item once someone is signed in, behind their PIN if they have one.
/// </summary>
public static class ItemLinks
{
    private const string Prefix = "etchasketch://item/";

    private static readonly ConcurrentQueue<Guid> Opened = new();

    /// <summary>Raised on whichever thread asked, when an item is waiting to be opened.</summary>
    public static event Action? Requested;

    /// <summary>The item a link points to, or null when it isn't an item link.</summary>
    public static Guid? Parse(string? link) =>
        link is not null && link.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(link.AsSpan(Prefix.Length).TrimEnd('/'), out var id)
            ? id
            : null;

    public static void RequestOpen(Guid itemId)
    {
        Opened.Enqueue(itemId);
        Requested?.Invoke();
    }

    /// <summary>Takes every waiting item, oldest first.</summary>
    public static IReadOnlyList<Guid> TakeOpened()
    {
        var taken = new List<Guid>();
        while (Opened.TryDequeue(out var id))
        {
            taken.Add(id);
        }

        return taken;
    }
}
