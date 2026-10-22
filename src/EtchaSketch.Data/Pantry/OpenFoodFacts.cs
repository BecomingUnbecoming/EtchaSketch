using System.Net;
using System.Text.Json;
using EtchaSketch.Core.Pantry;

namespace EtchaSketch.Data.Pantry;

/// <summary>
/// Open Food Facts (openfoodfacts.org), an open database of food products anyone can use. Asked
/// only when someone presses "Look up", and only the barcode is sent.
/// </summary>
public sealed class OpenFoodFacts(HttpClient http) : IProductLookup
{
    public static readonly Uri Address = new("https://world.openfoodfacts.org/");

    /// <summary>Open Food Facts asks every app to say who it is.</summary>
    public const string UserAgent = "EtchaSketch/1.0 (https://github.com/BecomingUnbecoming/EtchaSketch)";

    public static OpenFoodFacts Create()
    {
        var http = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return new OpenFoodFacts(http);
    }

    public async Task<ProductInfo?> LookupAsync(string barcode, CancellationToken cancellationToken = default)
    {
        if (Barcode.Normalize(barcode) is not { } digits)
        {
            throw new ArgumentException("That isn't a barcode.", nameof(barcode));
        }

        using var response = await http.GetAsync($"api/v2/product/{digits}.json?fields=product_name,brands,quantity", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return Read(document.RootElement);
    }

    /// <summary>The product in an answer, or null when it says it has none or has no name for it.</summary>
    public static ProductInfo? Read(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() != 1
            || !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = Text(product, "product_name");
        return name is null
            ? null
            // "brands" is a comma-separated list; the first is the one on the front of the pack.
            : new ProductInfo(name, Text(product, "brands")?.Split(',')[0].Trim(), Text(product, "quantity"));
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;
}
