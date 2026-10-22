namespace EtchaSketch.Core.Pantry;

/// <summary>Product barcodes: EAN-8, UPC-A (12), EAN-13 and GTIN-14, the kinds printed on shop goods.</summary>
public static class Barcode
{
    /// <summary>
    /// The barcode's digits if it is one of those kinds and its last digit checks out; null
    /// otherwise. Spaces and dashes are ignored, so "4 006381 333931" is fine.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.Where(c => c is not (' ' or '-')).ToArray());
        if (digits.Length is not (8 or 12 or 13 or 14) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        // GS1 check digit: from the right, ignoring the check digit, weights alternate 3, 1, 3…
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            sum += (digits[i] - '0') * ((digits.Length - 2 - i) % 2 == 0 ? 3 : 1);
        }

        return (10 - sum % 10) % 10 == digits[^1] - '0' ? digits : null;
    }
}

/// <summary>What an open product database knows about a barcode.</summary>
/// <param name="Name">Like "Oat drink".</param>
/// <param name="Brand">Like "Oatly", if known.</param>
/// <param name="Quantity">The pack size as printed, like "1 l", if known.</param>
public sealed record ProductInfo(string Name, string? Brand, string? Quantity)
{
    /// <summary>"Oatly Oat drink", or just the name when there's no brand or the name already has it.</summary>
    public string DisplayName =>
        Brand is { Length: > 0 } brand && !Name.Contains(brand, StringComparison.CurrentCultureIgnoreCase) ? $"{brand} {Name}" : Name;
}

/// <summary>Looks a barcode up somewhere online. Only the barcode is sent.</summary>
public interface IProductLookup
{
    /// <summary>The product, or null when the database doesn't know it. Throws when it can't be reached.</summary>
    Task<ProductInfo?> LookupAsync(string barcode, CancellationToken cancellationToken = default);
}
