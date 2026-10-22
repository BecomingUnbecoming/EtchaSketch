using System.Globalization;
using EtchaSketch.Core.Notifications;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Notifications;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Pantry;

/// <summary>What the household's things need every so often, and each thing's log: fuel, services, repairs, charges.</summary>
public sealed class UpkeepRepository(SqliteConnection connection, InventoryRepository inventory)
{
    private const string UpkeepColumns = "id, household_id, item_id, name, every_days, every_distance, last_done_on, last_done_distance, tier";
    private const string LogColumns = "id, household_id, item_id, kind, on_date, distance, amount, cost, note, upkeep_id, tier";

    public Upkeep AddUpkeep(Upkeep upkeep)
    {
        upkeep = Validated(upkeep);
        Execute($"INSERT INTO upkeep ({UpkeepColumns}, created_at) VALUES ($id, $household, $item, $name, $days, $distance, $doneOn, $doneAt, $tier, $created)",
            [.. Parameters(upkeep), ("$created", Now())]);
        return upkeep;
    }

    public Upkeep UpdateUpkeep(Upkeep upkeep)
    {
        upkeep = Validated(upkeep);
        var changed = Execute(
            """
            UPDATE upkeep SET name = $name, every_days = $days, every_distance = $distance, last_done_on = $doneOn,
                last_done_distance = $doneAt, tier = $tier
            WHERE id = $id
            """,
            Parameters(upkeep));
        return changed == 0 ? throw new ArgumentException($"There is no upkeep {upkeep.Id}.", nameof(upkeep)) : upkeep;
    }

    public void DeleteUpkeep(Guid id) => Execute("DELETE FROM upkeep WHERE id = $id", ("$id", id.ToString()));

    public Upkeep? GetUpkeep(Guid id) => QueryUpkeep($"SELECT {UpkeepColumns} FROM upkeep WHERE id = $id", ("$id", id.ToString())).SingleOrDefault();

    /// <summary>One item's upkeep, by name.</summary>
    public IReadOnlyList<Upkeep> UpkeepFor(Guid itemId) =>
        QueryUpkeep($"SELECT {UpkeepColumns} FROM upkeep WHERE item_id = $item ORDER BY name COLLATE NOCASE", ("$item", itemId.ToString()));

    /// <summary>
    /// Adds an entry to an item's log. An entry for an upkeep marks it done on that day, at that
    /// distance, unless it was already done more recently.
    /// </summary>
    public ItemLogEntry Log(ItemLogEntry entry)
    {
        entry = Validated(entry);
        using var transaction = connection.BeginTransaction();
        Execute($"INSERT INTO item_log ({LogColumns}, created_at) VALUES ($id, $household, $item, $kind, $on, $distance, $amount, $cost, $note, $upkeep, $tier, $created)",
            [.. Parameters(entry), ("$created", Now())]);

        if (entry.UpkeepId is { } upkeepId && GetUpkeep(upkeepId) is { } upkeep && upkeep.ItemId == entry.ItemId && entry.On >= upkeep.LastDoneOn)
        {
            Execute("UPDATE upkeep SET last_done_on = $on, last_done_distance = $distance WHERE id = $id",
                ("$id", upkeep.Id.ToString()), ("$on", Date(entry.On)), ("$distance", entry.Distance ?? DistanceNow(entry.ItemId) ?? upkeep.LastDoneDistance));
        }

        transaction.Commit();
        return entry;
    }

    public void DeleteLog(Guid id) => Execute("DELETE FROM item_log WHERE id = $id", ("$id", id.ToString()));

    /// <summary>An item's log, newest first.</summary>
    public IReadOnlyList<ItemLogEntry> LogFor(Guid itemId) =>
        QueryLog($"SELECT {LogColumns} FROM item_log WHERE item_id = $item ORDER BY on_date DESC, distance DESC, created_at DESC", ("$item", itemId.ToString()));

    /// <summary>The highest distance in a vehicle's log, or null before any entry has one.</summary>
    public int? DistanceNow(Guid itemId) =>
        Scalar("SELECT MAX(distance) FROM item_log WHERE item_id = $item", ("$item", itemId.ToString())) is long distance ? (int)distance : null;

    /// <summary>How many times an upkeep has been logged, like a battery's charge cycles.</summary>
    public int TimesDone(Guid upkeepId) =>
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM item_log WHERE upkeep_id = $upkeep", ("$upkeep", upkeepId.ToString())), CultureInfo.InvariantCulture);

    /// <summary>Everything spent on an item in its log.</summary>
    public decimal TotalCost(Guid itemId) =>
        LogFor(itemId).Sum(entry => entry.Cost ?? 0);

    /// <summary>
    /// Tells the household when upkeep is due: each person once each time it comes due, however
    /// many devices check. Doing it and coming due again warns again.
    /// </summary>
    public int CheckDue(Household household, NotificationRepository notifications, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var items = inventory.Items(household.Id).ToDictionary(item => item.Id);
        var added = 0;
        foreach (var upkeep in QueryUpkeep($"SELECT {UpkeepColumns} FROM upkeep WHERE household_id = $household AND tier <= $shared",
                     ("$household", household.Id.ToString()), ("$shared", (int)PrivacyTier.Household)))
        {
            if (!items.TryGetValue(upkeep.ItemId, out var item))
            {
                continue;
            }

            var distanceNow = DistanceNow(item.Id);
            if (!upkeep.IsDue(today, distanceNow))
            {
                continue;
            }

            var reason = upkeep.NextAtDistance is { } at && distanceNow >= at
                ? $"Every {Number(upkeep.EveryDistance!.Value)} {item.DistanceUnit} · now at {Number(distanceNow!.Value)} {item.DistanceUnit}"
                : $"Every {upkeep.EveryDays} days · last done {upkeep.LastDoneOn.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
            foreach (var member in household.Members)
            {
                var notification = new Notification
                {
                    OwnerId = member.Id,
                    Kind = NotificationKind.UpkeepDue,
                    Title = $"{item.Name}: {upkeep.Name} is due",
                    Body = reason,
                    CreatedAt = now,
                    Tier = PrivacyTier.Household,
                };
                if (notifications.Add(notification, $"upkeep:{upkeep.Id}:{upkeep.LastDoneOn:yyyy-MM-dd}:{upkeep.LastDoneDistance}") is not null)
                {
                    added++;
                }
            }
        }

        return added;
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private Upkeep Validated(Upkeep upkeep)
    {
        var name = upkeep.Name?.Trim() ?? "";
        if (name.Length is 0 or > PantryRepository.MaxNameLength)
        {
            throw new ArgumentException("Give it a name, like Oil change.", nameof(upkeep));
        }

        var item = inventory.Get(upkeep.ItemId) ?? throw new ArgumentException("That item isn't in the inventory.", nameof(upkeep));
        if (upkeep.EveryDays is null && upkeep.EveryDistance is null)
        {
            throw new ArgumentException("Say how often: every so many days, or every so many km or miles.", nameof(upkeep));
        }

        if (upkeep.EveryDays is <= 0 || upkeep.EveryDistance is <= 0 || upkeep.LastDoneDistance is < 0)
        {
            throw new ArgumentException("Use numbers above zero.", nameof(upkeep));
        }

        if (upkeep.EveryDistance is not null && !item.IsVehicle)
        {
            throw new ArgumentException("Only a vehicle has a distance. Use days, or mark it as a vehicle.", nameof(upkeep));
        }

        return upkeep with
        {
            Name = name,
            HouseholdId = item.HouseholdId,
            Tier = upkeep.Tier < PrivacyTier.Household ? PrivacyTier.Household : upkeep.Tier,
        };
    }

    private ItemLogEntry Validated(ItemLogEntry entry)
    {
        var item = inventory.Get(entry.ItemId) ?? throw new ArgumentException("That item isn't in the inventory.", nameof(entry));
        if (entry.Distance is < 0 || entry.Amount is < 0 || entry.Cost is < 0)
        {
            throw new ArgumentException("Use numbers of zero or more.", nameof(entry));
        }

        if (entry.Distance is not null && !item.IsVehicle)
        {
            throw new ArgumentException("Only a vehicle has a distance.", nameof(entry));
        }

        var note = entry.Note?.Trim();
        return entry with
        {
            HouseholdId = item.HouseholdId,
            Note = string.IsNullOrEmpty(note) ? null : note.Length > 1000 ? throw new ArgumentException("Keep the note to 1000 characters.", nameof(entry)) : note,
            Tier = entry.Tier < PrivacyTier.Household ? PrivacyTier.Household : entry.Tier,
        };
    }

    private static (string, object?)[] Parameters(Upkeep upkeep) =>
    [
        ("$id", upkeep.Id.ToString()), ("$household", upkeep.HouseholdId.ToString()), ("$item", upkeep.ItemId.ToString()), ("$name", upkeep.Name),
        ("$days", upkeep.EveryDays), ("$distance", upkeep.EveryDistance), ("$doneOn", Date(upkeep.LastDoneOn)), ("$doneAt", upkeep.LastDoneDistance),
        ("$tier", (int)upkeep.Tier),
    ];

    private static (string, object?)[] Parameters(ItemLogEntry entry) =>
    [
        ("$id", entry.Id.ToString()), ("$household", entry.HouseholdId.ToString()), ("$item", entry.ItemId.ToString()), ("$kind", entry.Kind.ToString()),
        ("$on", Date(entry.On)), ("$distance", entry.Distance), ("$amount", Decimal(entry.Amount)), ("$cost", Decimal(entry.Cost)),
        ("$note", entry.Note), ("$upkeep", entry.UpkeepId?.ToString()), ("$tier", (int)entry.Tier),
    ];

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Decimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static DateOnly ReadDate(SqliteDataReader reader, int column) =>
        DateOnly.ParseExact(reader.GetString(column), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int? ReadInt(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);

    private static decimal? ReadDecimal(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : decimal.Parse(reader.GetString(column), NumberStyles.Number, CultureInfo.InvariantCulture);

    private List<Upkeep> QueryUpkeep(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var found = new List<Upkeep>();
        while (reader.Read())
        {
            found.Add(new Upkeep
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                ItemId = Guid.Parse(reader.GetString(2)),
                Name = reader.GetString(3),
                EveryDays = ReadInt(reader, 4),
                EveryDistance = ReadInt(reader, 5),
                LastDoneOn = ReadDate(reader, 6),
                LastDoneDistance = ReadInt(reader, 7),
                Tier = (PrivacyTier)reader.GetInt32(8),
            });
        }

        return found;
    }

    private List<ItemLogEntry> QueryLog(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var found = new List<ItemLogEntry>();
        while (reader.Read())
        {
            found.Add(new ItemLogEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                HouseholdId = Guid.Parse(reader.GetString(1)),
                ItemId = Guid.Parse(reader.GetString(2)),
                // A kind from a newer version reads as a note rather than breaking the log.
                Kind = Enum.TryParse<LogKind>(reader.GetString(3), out var kind) ? kind : LogKind.Note,
                On = ReadDate(reader, 4),
                Distance = ReadInt(reader, 5),
                Amount = ReadDecimal(reader, 6),
                Cost = ReadDecimal(reader, 7),
                Note = reader.IsDBNull(8) ? null : reader.GetString(8),
                UpkeepId = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                Tier = (PrivacyTier)reader.GetInt32(10),
            });
        }

        return found;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
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
