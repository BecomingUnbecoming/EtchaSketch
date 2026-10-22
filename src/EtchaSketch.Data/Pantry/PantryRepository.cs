using System.Globalization;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Pantry;

/// <summary>
/// The household's stock and shopping list. Ticking a shopping line that came from stock puts the
/// amount back in stock; using something takes it out, and what runs low can go on the list.
/// </summary>
public sealed class PantryRepository(SqliteConnection connection)
{
    public const int MaxNameLength = 100;
    public const int MaxUnitLength = 20;
    public const int MaxLocationLength = 60;

    private const string StockColumns = "id, household_id, name, amount, unit, location, best_before, low_at, barcode, tier";
    private const string ShoppingColumns = "id, household_id, name, amount, unit, stock_item_id, bought_at, tier";

    public StockItem AddStock(StockItem item)
    {
        item = Validated(item);
        Execute($"INSERT INTO stock_items ({StockColumns}, created_at) VALUES ($id, $household, $name, $amount, $unit, $location, $best, $low, $barcode, $tier, $created)",
            [.. StockParameters(item), ("$created", Text(DateTimeOffset.UtcNow))]);
        return item;
    }

    public StockItem UpdateStock(StockItem item)
    {
        item = Validated(item);
        var changed = Execute(
            """
            UPDATE stock_items SET household_id = $household, name = $name, amount = $amount, unit = $unit, location = $location,
                best_before = $best, low_at = $low, barcode = $barcode, tier = $tier
            WHERE id = $id
            """,
            StockParameters(item));
        return changed == 0 ? throw new ArgumentException($"There is no stock item {item.Id}.", nameof(item)) : item;
    }

    /// <summary>Removes a stock item. Shopping lines that topped it up stay on the list as plain lines.</summary>
    public void DeleteStock(Guid id)
    {
        Execute("UPDATE shopping_items SET stock_item_id = NULL WHERE stock_item_id = $id", ("$id", id.ToString()));
        Execute("DELETE FROM stock_items WHERE id = $id", ("$id", id.ToString()));
    }

    public StockItem? GetStock(Guid id) => QueryStock($"SELECT {StockColumns} FROM stock_items WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    /// <summary>The household's stock, by place then name.</summary>
    public IReadOnlyList<StockItem> Stock(Guid householdId) => QueryStock(
        $"SELECT {StockColumns} FROM stock_items WHERE household_id = $household AND tier <= $shared ORDER BY location IS NULL, location, name COLLATE NOCASE",
        ("$household", householdId.ToString()), ("$shared", (int)PrivacyTier.Household));

    /// <summary>Takes some out of stock (it never goes below nothing).</summary>
    public StockItem Use(Guid id, decimal amount)
    {
        var item = GetStock(id) ?? throw new ArgumentException($"There is no stock item {id}.", nameof(id));
        return UpdateStock(item with { Amount = Math.Max(0, item.Amount - Math.Abs(amount)) });
    }

    /// <summary>The stock item with this barcode, so scanning something already in stock finds it.</summary>
    public StockItem? FindByBarcode(Guid householdId, string barcode) =>
        Stock(householdId).FirstOrDefault(item => item.Barcode == barcode);

    /// <summary>What has run low.</summary>
    public IReadOnlyList<StockItem> Low(Guid householdId) => Stock(householdId).Where(item => item.IsLow).ToList();

    /// <summary>What's in stock with a best-before date on or before a day.</summary>
    public IReadOnlyList<StockItem> ExpiringBy(Guid householdId, DateOnly day) =>
        Stock(householdId).Where(item => item.Amount > 0 && item.BestBefore is { } date && date <= day).OrderBy(item => item.BestBefore).ToList();

    /// <summary>
    /// Adds a line to the shopping list, unless the same thing is already on it waiting to be
    /// bought; then that line is returned instead.
    /// </summary>
    public ShoppingItem AddToList(ShoppingItem item)
    {
        var name = Name(item.Name);
        if (item.Amount is < 0)
        {
            throw new ArgumentException("An amount can't be less than nothing.", nameof(item));
        }

        if (OpenList(item.HouseholdId).FirstOrDefault(open =>
                (item.StockItemId is not null && open.StockItemId == item.StockItemId)
                || string.Equals(open.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } existing)
        {
            return existing;
        }

        item = item with { Name = name, Unit = Short(item.Unit, MaxUnitLength, "A unit"), BoughtAt = null };
        Execute($"INSERT INTO shopping_items ({ShoppingColumns}, created_at) VALUES ($id, $household, $name, $amount, $unit, $stock, $bought, $tier, $created)",
            [.. ShoppingParameters(item), ("$created", Text(DateTimeOffset.UtcNow))]);
        return item;
    }

    /// <summary>The list: what's still to buy, then what was bought, each in the order it was added.</summary>
    public IReadOnlyList<ShoppingItem> ShoppingList(Guid householdId) => QueryShopping(
        $"SELECT {ShoppingColumns} FROM shopping_items WHERE household_id = $household AND tier <= $shared ORDER BY bought_at IS NOT NULL, rowid",
        ("$household", householdId.ToString()), ("$shared", (int)PrivacyTier.Household));

    /// <summary>Ticks a line off. If it tops up a stock item, that amount (or one) goes into stock.</summary>
    public void Tick(Guid shoppingItemId, DateTimeOffset at) => SetBought(shoppingItemId, at);

    /// <summary>Unticks a line, taking back out of stock what ticking it put in.</summary>
    public void Untick(Guid shoppingItemId) => SetBought(shoppingItemId, null);

    /// <summary>Puts everything that has run low on the list. Returns how many lines were added.</summary>
    public int AddLowToList(Guid householdId)
    {
        var open = OpenList(householdId);
        var added = 0;
        foreach (var item in Low(householdId).Where(item => open.All(line => line.StockItemId != item.Id)))
        {
            AddToList(new ShoppingItem { HouseholdId = householdId, Name = item.Name, Unit = item.Unit, StockItemId = item.Id });
            added++;
        }

        return added;
    }

    /// <summary>Takes ticked lines off the list.</summary>
    public int ClearBought(Guid householdId) =>
        Execute("DELETE FROM shopping_items WHERE household_id = $household AND bought_at IS NOT NULL", ("$household", householdId.ToString()));

    public void DeleteFromList(Guid shoppingItemId) => Execute("DELETE FROM shopping_items WHERE id = $id", ("$id", shoppingItemId.ToString()));

    private void SetBought(Guid shoppingItemId, DateTimeOffset? at)
    {
        var line = QueryShopping($"SELECT {ShoppingColumns} FROM shopping_items WHERE id = $id", ("$id", shoppingItemId.ToString())).SingleOrDefault()
                   ?? throw new ArgumentException($"There is no shopping line {shoppingItemId}.", nameof(shoppingItemId));
        if ((line.BoughtAt is null) == (at is null))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        Execute("UPDATE shopping_items SET bought_at = $at WHERE id = $id", ("$id", line.Id.ToString()), ("$at", at is { } time ? Text(time) : null));
        if (line.StockItemId is { } stockId && GetStock(stockId) is { } stock)
        {
            var amount = line.Amount ?? 1;
            UpdateStock(stock with { Amount = Math.Max(0, stock.Amount + (at is null ? -amount : amount)) });
        }

        transaction.Commit();
    }

    private List<ShoppingItem> OpenList(Guid householdId) => ShoppingList(householdId).Where(line => line.BoughtAt is null).ToList();

    private StockItem Validated(StockItem item)
    {
        if (item.Amount < 0 || item.LowAt is < 0)
        {
            throw new ArgumentException("An amount can't be less than nothing.", nameof(item));
        }

        return item with
        {
            Name = Name(item.Name),
            Unit = Short(item.Unit, MaxUnitLength, "A unit"),
            Location = Short(item.Location, MaxLocationLength, "A place"),
            Barcode = Short(item.Barcode, 64, "A barcode"),
            // The pantry is the household's; a private item would never show on the shared list.
            Tier = item.Tier < PrivacyTier.Household ? PrivacyTier.Household : item.Tier,
        };
    }

    private static string Name(string? name)
    {
        name = name?.Trim() ?? "";
        return name.Length is 0 or > MaxNameLength
            ? throw new ArgumentException(name.Length == 0 ? "Give it a name." : $"A name can be at most {MaxNameLength} characters.")
            : name;
    }

    private static string? Short(string? text, int max, string what)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null
            : text.Length > max ? throw new ArgumentException($"{what} can be at most {max} characters.") : text;
    }

    private static (string, object?)[] StockParameters(StockItem item) =>
    [
        ("$id", item.Id.ToString()), ("$household", item.HouseholdId.ToString()), ("$name", item.Name), ("$amount", Number(item.Amount)),
        ("$unit", item.Unit), ("$location", item.Location), ("$best", item.BestBefore?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ("$low", item.LowAt is { } low ? Number(low) : null), ("$barcode", item.Barcode), ("$tier", (int)item.Tier),
    ];

    private static (string, object?)[] ShoppingParameters(ShoppingItem item) =>
    [
        ("$id", item.Id.ToString()), ("$household", item.HouseholdId.ToString()), ("$name", item.Name),
        ("$amount", item.Amount is { } amount ? Number(amount) : null), ("$unit", item.Unit), ("$stock", item.StockItemId?.ToString()),
        ("$bought", item.BoughtAt is { } at ? Text(at) : null), ("$tier", (int)item.Tier),
    ];

    private List<StockItem> QueryStock(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var items = new List<StockItem>();
        while (reader.Read())
        {
            items.Add(new StockItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                Name = reader.GetString(2),
                Amount = ParseNumber(reader.GetString(3)),
                Unit = reader.IsDBNull(4) ? null : reader.GetString(4),
                Location = reader.IsDBNull(5) ? null : reader.GetString(5),
                BestBefore = reader.IsDBNull(6) ? null : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                LowAt = reader.IsDBNull(7) ? null : ParseNumber(reader.GetString(7)),
                Barcode = reader.IsDBNull(8) ? null : reader.GetString(8),
                Tier = (PrivacyTier)reader.GetInt32(9),
            });
        }

        return items;
    }

    private List<ShoppingItem> QueryShopping(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var items = new List<ShoppingItem>();
        while (reader.Read())
        {
            items.Add(new ShoppingItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                Name = reader.GetString(2),
                Amount = reader.IsDBNull(3) ? null : ParseNumber(reader.GetString(3)),
                Unit = reader.IsDBNull(4) ? null : reader.GetString(4),
                StockItemId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                BoughtAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Tier = (PrivacyTier)reader.GetInt32(7),
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

    // Amounts are kept as exact decimal text, so half a pack stays half a pack on every device.
    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal ParseNumber(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string Text(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
