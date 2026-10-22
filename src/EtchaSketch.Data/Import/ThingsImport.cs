using System.Globalization;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Data.Pantry;

namespace EtchaSketch.Data.Import;

/// <summary>What an import would do, or did.</summary>
/// <param name="Added">New things or log entries.</param>
/// <param name="AlreadyThere">Ones brought in by an earlier import.</param>
/// <param name="Skipped">Each row left out, with why: "Row 4: archived in Homebox".</param>
/// <param name="Note">Anything else worth saying, like how dates were read.</param>
public sealed record ThingsImportResult(int Added, int AlreadyThere, IReadOnlyList<string> Skipped, string? Note = null);

/// <summary>
/// Brings in Homebox's CSV export (Tools → Export inventory) as the household's things. Each one
/// remembers where it came from, so importing the same file again adds nothing.
/// </summary>
public static class HomeboxImport
{
    private const string Marker = "Imported from Homebox";

    public static ThingsImportResult Apply(InventoryRepository inventory, Guid householdId, string csv, bool dryRun = false)
    {
        var (columns, rows) = CsvRow.Sheet(csv);
        if (!columns.ContainsKey("HB.name"))
        {
            throw new FormatException("That isn't a Homebox export: it has no HB.name column. In Homebox, use Tools → Export inventory.");
        }

        var existing = inventory.Items(householdId).Select(item => item.Notes).OfType<string>()
            .SelectMany(notes => notes.Split('\n')).Where(line => line.StartsWith(Marker, StringComparison.Ordinal)).ToHashSet();
        var added = 0;
        var already = 0;
        var skipped = new List<string>();
        foreach (var row in rows)
        {
            if (row["HB.name"] is not { } name)
            {
                skipped.Add($"Row {row.Number}: no name");
                continue;
            }

            if (Truthy(row["HB.archived"]))
            {
                skipped.Add($"Row {row.Number}: {name} is archived in Homebox");
                continue;
            }

            if (Date(row["HB.sold_date", "HB.sold_time"]) is not null || row["HB.sold_to"] is not null)
            {
                skipped.Add($"Row {row.Number}: {name} was sold");
                continue;
            }

            var reference = row["HB.asset_id"] is { } asset && asset.Trim('0', '-').Length > 0 ? $"asset {asset}"
                : row["HB.import_ref"] is { } importRef ? $"ref {importRef}"
                : $"row {row.Number}, {name}";
            var marker = $"{Marker} ({reference})";
            if (!existing.Add(marker))
            {
                already++;
                continue;
            }

            var quantity = Number(row["HB.quantity"]);
            string?[] notes =
            [
                row["HB.description"],
                row["HB.notes"],
                string.Join(" ", new[] { row["HB.manufacturer"], row["HB.model_number"] }.OfType<string>()) is { Length: > 0 } make ? $"Make: {make}" : null,
                quantity is > 1 ? $"Quantity: {quantity.Value.ToString("0.##", CultureInfo.InvariantCulture)}" : null,
                row["HB.purchase_from"] is { } from ? $"Bought from: {from}" : null,
                Truthy(row["HB.lifetime_warranty"]) ? "Lifetime warranty" : null,
                row["HB.warranty_details"] is { } warranty ? $"Warranty: {warranty}" : null,
                Truthy(row["HB.insured"]) ? "Insured" : null,
                row["HB.url"] is { } url ? $"Link: {url}" : null,
                .. columns.Keys.Where(column => column.StartsWith("HB.field.", StringComparison.OrdinalIgnoreCase))
                    .Select(column => row[column] is { } value ? $"{column["HB.field.".Length..]}: {value}" : null),
            ];

            try
            {
                var item = new Core.Pantry.InventoryItem
                {
                    HouseholdId = householdId,
                    Name = Fit(name, PantryRepository.MaxNameLength),
                    Location = Place(row["HB.location"]),
                    Category = row["HB.tags", "HB.labels"]?.Split(';')[0].Trim() is { Length: > 0 } tag ? Fit(tag, 40) : null,
                    Serial = row["HB.serial_number"] is { } serial ? Fit(serial, 100) : null,
                    PurchasedOn = Date(row["HB.purchase_date", "HB.purchase_time"]),
                    Price = Number(row["HB.purchase_price"]) is > 0 and var price ? price : null,
                    WarrantyEnds = Date(row["HB.warranty_expires"]),
                    // The marker goes last and must survive, so the notes are cut from the front of the rest.
                    Notes = KeepMarker(string.Join("\n", notes.OfType<string>()), marker),
                };
                if (!dryRun)
                {
                    inventory.Add(item);
                }

                added++;
            }
            catch (ArgumentException exception)
            {
                skipped.Add($"Row {row.Number}: {name}: {exception.Message.Split(" (Parameter")[0]}");
            }
        }

        return new ThingsImportResult(added, already, skipped);
    }

    // Homebox writes places as a path, "Home / Garage / Shelf 2". Kept whole when it fits.
    private static string? Place(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var whole = string.Join(" / ", parts);
        return whole.Length <= 60 ? whole : Fit(parts[^1], 60);
    }

    private static string KeepMarker(string rest, string marker)
    {
        const int Max = 4000;
        var room = Max - marker.Length - 1;
        return rest.Length == 0 ? marker : $"{(rest.Length > room ? rest[..room] : rest)}\n{marker}";
    }

    private static string Fit(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    private static bool Truthy(string? text) => text is not null && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1");

    // Homebox writes an empty date as 0001-01-01.
    private static DateOnly? Date(string? text) =>
        text is not null && DateOnly.TryParseExact(text.Length >= 10 ? text[..10] : text, ["yyyy-MM-dd", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date.Year > 1
            ? date
            : null;

    private static decimal? Number(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// Brings in a LubeLogger CSV export (a vehicle's fuel, service, repair or upgrade records) as a
/// vehicle's log. Entries already in the log (same day, kind, distance and cost) are left out.
/// </summary>
public static class LubeLoggerImport
{
    public static ThingsImportResult Apply(UpkeepRepository upkeep, Core.Pantry.InventoryItem vehicle, LogKind kind, string csv, bool dryRun = false)
    {
        if (!vehicle.IsVehicle)
        {
            throw new FormatException($"{vehicle.Name} isn't marked as a vehicle. Edit it and tick \"It's a vehicle\" first.");
        }

        var (columns, rows) = CsvRow.Sheet(csv);
        if (!columns.ContainsKey("Date"))
        {
            throw new FormatException("That isn't a LubeLogger export: it has no Date column.");
        }

        var isFuelFile = columns.ContainsKey("FuelConsumed");
        if (kind == LogKind.Fuel && !isFuelFile)
        {
            throw new FormatException("That file has no FuelConsumed column, so it isn't fuel records. Choose what kind of records it holds.");
        }

        if (isFuelFile)
        {
            kind = LogKind.Fuel;
        }

        var dayFirst = DayComesFirst(rows.Select(row => row["Date"]).OfType<string>());
        var existing = upkeep.LogFor(vehicle.Id).Select(entry => (entry.On, entry.Kind, entry.Distance, entry.Cost)).ToHashSet();
        var added = 0;
        var already = 0;
        var skipped = new List<string>();
        foreach (var row in rows)
        {
            if (ReadDate(row["Date"], dayFirst) is not { } on)
            {
                skipped.Add($"Row {row.Number}: can't read the date \"{row["Date"]}\"");
                continue;
            }

            var distance = Whole(row["Odometer"]);
            var cost = Money(row["Cost"]);
            if (!existing.Add((on, kind, distance, cost)))
            {
                already++;
                continue;
            }

            var note = string.Join(" · ", new[]
            {
                row["Description"],
                row["Notes"],
                kind == LogKind.Fuel && row["IsFillToFull"] is { } full && full.Equals("false", StringComparison.OrdinalIgnoreCase) ? "partial fill" : null,
                kind == LogKind.Fuel && row["MissedFuelUp"] is { } missed && missed.Equals("true", StringComparison.OrdinalIgnoreCase) ? "missed a fill-up before this" : null,
                row["Tags"] is { } tags ? $"tags: {tags}" : null,
            }.OfType<string>());

            try
            {
                var entry = new ItemLogEntry
                {
                    HouseholdId = vehicle.HouseholdId,
                    ItemId = vehicle.Id,
                    Kind = kind,
                    On = on,
                    Distance = distance,
                    Amount = kind == LogKind.Fuel ? Money(row["FuelConsumed"]) : null,
                    Cost = cost,
                    Note = note.Length == 0 ? null : note.Length > 1000 ? note[..1000] : note,
                };
                if (!dryRun)
                {
                    upkeep.Log(entry);
                }

                added++;
            }
            catch (ArgumentException exception)
            {
                skipped.Add($"Row {row.Number}: {exception.Message.Split(" (Parameter")[0]}");
            }
        }

        var note2 = dayFirst is { } first ? $"Dates were read as {(first ? "day/month/year" : "month/day/year")}." : null;
        return new ThingsImportResult(added, already, skipped, note2);
    }

    /// <summary>
    /// LubeLogger writes dates in its server's own format. For dates with slashes or dots, the file
    /// decides: a date that only works day first (21/10) means day first, one that only works month
    /// first (10/21) means month first. If nothing tells, month first, LubeLogger's default (US
    /// English). Null when the dates are ISO.
    /// </summary>
    public static bool? DayComesFirst(IEnumerable<string> dates)
    {
        bool? found = null;
        var any = false;
        foreach (var date in dates)
        {
            var parts = date.Split(['/', '.', '-'], 3);
            if (parts.Length < 3 || parts[0].Length == 4)
            {
                continue;
            }

            any = true;
            if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || a < 1 || b < 1)
            {
                continue;
            }

            // Only a date that works one way round tells; "13/45" works neither way and says nothing.
            if (a > 12 && a <= 31 && b <= 12)
            {
                found = true;
            }
            else if (b > 12 && b <= 31 && a <= 12)
            {
                found = false;
            }
        }

        return any ? found ?? false : null;
    }

    private static DateOnly? ReadDate(string? text, bool? dayFirst)
    {
        if (text is null)
        {
            return null;
        }

        text = text.Split(' ', 'T')[0];
        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        var parts = text.Split(['/', '.', '-']);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || !int.TryParse(parts[2], out var year))
        {
            return null;
        }

        var (day, month) = dayFirst == true ? (a, b) : (b, a);
        year = year < 100 ? 2000 + year : year;
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    private static int? Whole(string? text) =>
        text is null ? null : int.TryParse(new string(text.Where(char.IsAsciiDigit).ToArray()), out var number) && text.Any(char.IsAsciiDigit) ? number : null;

    /// <summary>"$1,234.50", "1.234,50 €" and "61.2" all read as numbers; whichever of , or . comes last is the decimal point.</summary>
    public static decimal? Money(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var kept = new string(text.Where(c => char.IsAsciiDigit(c) || c is ',' or '.' or '-').ToArray());
        var point = Math.Max(kept.LastIndexOf('.'), kept.LastIndexOf(','));
        if (point >= 0 && kept.Length - point - 1 == 3 && kept.IndexOfAny([',', '.']) == point && kept.Count(c => c is ',' or '.') == 1 && kept[point] == ',')
        {
            // "1,234" with nothing else is a thousands separator, not a decimal point.
            point = -1;
        }

        var normal = point < 0
            ? new string(kept.Where(c => c is not (',' or '.')).ToArray())
            : new string(kept[..point].Where(c => c is not (',' or '.')).ToArray()) + "." + kept[(point + 1)..];
        return decimal.TryParse(normal, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
