using System.Globalization;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Notifications;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Pantry;

/// <summary>The household's things: what, where, warranty, and who has it.</summary>
public sealed class InventoryRepository(SqliteConnection connection)
{
    /// <summary>How many days before a warranty ends it warns.</summary>
    public const int WarrantyWarningDays = 30;

    private const string Columns = "id, household_id, name, location, category, serial, purchased_on, price, warranty_ends, manual, notes, borrowed_by, tier";

    public InventoryItem Add(InventoryItem item)
    {
        item = Validated(item);
        Execute($"INSERT INTO inventory_items ({Columns}, created_at) VALUES ($id, $household, $name, $location, $category, $serial, $purchased, $price, $warranty, $manual, $notes, $borrowed, $tier, $created)",
            [.. Parameters(item), ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))]);
        return item;
    }

    public InventoryItem Update(InventoryItem item)
    {
        item = Validated(item);
        var changed = Execute(
            """
            UPDATE inventory_items SET name = $name, location = $location, category = $category, serial = $serial, purchased_on = $purchased,
                price = $price, warranty_ends = $warranty, manual = $manual, notes = $notes, borrowed_by = $borrowed, tier = $tier
            WHERE id = $id
            """,
            Parameters(item));
        return changed == 0 ? throw new ArgumentException($"There is no item {item.Id}.", nameof(item)) : item;
    }

    public void Delete(Guid id) => Execute("DELETE FROM inventory_items WHERE id = $id", ("$id", id.ToString()));

    public InventoryItem? Get(Guid id) => Query($"SELECT {Columns} FROM inventory_items WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    /// <summary>The household's things, by place then name, narrowed to those matching some text if given.</summary>
    public IReadOnlyList<InventoryItem> Items(Guid householdId, string? search = null)
    {
        var items = Query($"SELECT {Columns} FROM inventory_items WHERE household_id = $household AND tier <= $shared ORDER BY location IS NULL, location, name COLLATE NOCASE",
            ("$household", householdId.ToString()), ("$shared", (int)PrivacyTier.Household));
        var text = search?.Trim();
        return string.IsNullOrEmpty(text)
            ? items
            : items.Where(item => new[] { item.Name, item.Location, item.Category, item.Serial, item.Notes }
                .Any(field => field?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true)).ToList();
    }

    /// <summary>
    /// Warns the household when a warranty is about to end: each person once per item and date,
    /// however many devices check.
    /// </summary>
    public int CheckWarranties(Household household, NotificationRepository notifications, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var added = 0;
        foreach (var item in Items(household.Id).Where(item => item.WarrantyEnds is { } end && end >= today && end <= today.AddDays(WarrantyWarningDays)))
        {
            var end = item.WarrantyEnds!.Value;
            var days = end.DayNumber - today.DayNumber;
            foreach (var member in household.Members)
            {
                var notification = new Notification
                {
                    OwnerId = member.Id,
                    Kind = NotificationKind.WarrantyEnding,
                    Title = days == 0 ? $"The warranty on {item.Name} ends today" : $"The warranty on {item.Name} ends in {days} days",
                    Body = $"Ends {end.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)}",
                    CreatedAt = now,
                    Tier = PrivacyTier.Household,
                };
                if (notifications.Add(notification, $"warranty:{item.Id}:{end:yyyy-MM-dd}") is not null)
                {
                    added++;
                }
            }
        }

        return added;
    }

    private static InventoryItem Validated(InventoryItem item)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length is 0 or > PantryRepository.MaxNameLength)
        {
            throw new ArgumentException("Give it a name.", nameof(item));
        }

        if (item.Price is < 0)
        {
            throw new ArgumentException("A price can't be less than nothing.", nameof(item));
        }

        return item with
        {
            Name = name,
            Location = Short(item.Location, 60),
            Category = Short(item.Category, 40),
            Serial = Short(item.Serial, 100),
            Manual = Short(item.Manual, 500),
            Notes = Short(item.Notes, 4000),
            Tier = item.Tier < PrivacyTier.Household ? PrivacyTier.Household : item.Tier,
        };
    }

    private static string? Short(string? text, int max)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? throw new ArgumentException($"Keep that to {max} characters.") : text;
    }

    private static (string, object?)[] Parameters(InventoryItem item) =>
    [
        ("$id", item.Id.ToString()), ("$household", item.HouseholdId.ToString()), ("$name", item.Name), ("$location", item.Location),
        ("$category", item.Category), ("$serial", item.Serial), ("$purchased", Date(item.PurchasedOn)),
        ("$price", item.Price?.ToString(CultureInfo.InvariantCulture)), ("$warranty", Date(item.WarrantyEnds)), ("$manual", item.Manual),
        ("$notes", item.Notes), ("$borrowed", item.BorrowedBy?.ToString()), ("$tier", (int)item.Tier),
    ];

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ReadDate(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : DateOnly.ParseExact(reader.GetString(column), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private List<InventoryItem> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var items = new List<InventoryItem>();
        while (reader.Read())
        {
            items.Add(new InventoryItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                Name = reader.GetString(2),
                Location = reader.IsDBNull(3) ? null : reader.GetString(3),
                Category = reader.IsDBNull(4) ? null : reader.GetString(4),
                Serial = reader.IsDBNull(5) ? null : reader.GetString(5),
                PurchasedOn = ReadDate(reader, 6),
                Price = reader.IsDBNull(7) ? null : decimal.Parse(reader.GetString(7), NumberStyles.Number, CultureInfo.InvariantCulture),
                WarrantyEnds = ReadDate(reader, 8),
                Manual = reader.IsDBNull(9) ? null : reader.GetString(9),
                Notes = reader.IsDBNull(10) ? null : reader.GetString(10),
                BorrowedBy = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
                Tier = (PrivacyTier)reader.GetInt32(12),
            });
        }

        return items;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
