using System.Globalization;
using System.Security;
using System.Text;
using EtchaSketch.Core.Pantry;
using QRCoder;

namespace EtchaSketch.UI.Services;

/// <summary>
/// A printable label for something the household owns: a QR code of its link with its name
/// underneath. Scanning the code on a phone with EtchaSketch opens the item. The code carries only
/// the item's id, so a stranger who scans it learns nothing about it.
/// </summary>
public static class ItemLabel
{
    private const int Cell = 8;
    private const int TextHeight = 40;

    public static string Svg(InventoryItem item)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(item.Link, QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;
        var size = modules.Count * Cell;

        var path = new StringBuilder();
        for (var row = 0; row < modules.Count; row++)
        {
            for (var column = 0; column < modules.Count; column++)
            {
                if (modules[row][column])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{column * Cell},{row * Cell}h{Cell}v{Cell}h-{Cell}z");
                }
            }
        }

        var name = SecurityElement.Escape(item.Name);
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size + TextHeight}" viewBox="0 0 {size} {size + TextHeight}">
              <rect width="100%" height="100%" fill="#fff"/>
              <path d="{path}" fill="#000"/>
              <text x="{size / 2}" y="{size + 24}" font-family="sans-serif" font-size="18" text-anchor="middle" fill="#000">{name}</text>
            </svg>
            """;
    }

    /// <summary>A file name for the label, like "label-drill.svg".</summary>
    public static string FileName(InventoryItem item)
    {
        var safe = new StringBuilder();
        foreach (var c in item.Name.ToLowerInvariant())
        {
            safe.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var name = safe.ToString().Trim('-');
        return $"label-{(name.Length == 0 ? "item" : name)}.svg";
    }
}
