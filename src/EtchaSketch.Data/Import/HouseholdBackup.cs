using System.Text.Json;
using System.Text.Json.Nodes;
using EtchaSketch.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Import;

/// <summary>What restoring a backup would add, or did, table by table.</summary>
/// <param name="Added">New rows, by what they are ("pantry items", "things"…).</param>
/// <param name="AlreadyThere">Rows already here, matched by their id.</param>
public sealed record HouseholdRestore(IReadOnlyDictionary<string, int> Added, int AlreadyThere)
{
    public int Total => Added.Values.Sum();
}

/// <summary>
/// The household's shared modules (pantry, shopping, recipes, meal plan, things, upkeep, logs) as
/// rows in the "Everything" export, and back. Rows keep their ids, so a restore brings each back as
/// itself and never doubles one up. Only what the whole household shares is in it.
/// </summary>
public static class HouseholdBackup
{
    /// <summary>The tables, parents before children, with what a person would call them.</summary>
    public static readonly (string Table, string Name)[] Tables =
    [
        ("stock_items", "pantry items"),
        ("shopping_items", "shopping list lines"),
        ("recipes", "recipes"),
        ("planned_meals", "planned meals"),
        ("inventory_items", "things"),
        ("upkeep", "upkeep schedules"),
        ("item_log", "log entries"),
    ];

    public static JsonObject Export(SqliteConnection connection, Guid householdId)
    {
        var backup = new JsonObject();
        foreach (var (table, _) in Tables)
        {
            var rows = new JsonArray();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} WHERE household_id = $household AND tier <= $shared ORDER BY rowid";
            command.Parameters.AddWithValue("$household", householdId.ToString());
            command.Parameters.AddWithValue("$shared", (int)PrivacyTier.Household);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                    {
                        string text => JsonValue.Create(text),
                        long number => JsonValue.Create(number),
                        double number => JsonValue.Create(number),
                        var other => JsonValue.Create(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)),
                    };
                }

                rows.Add(row);
            }

            backup[table] = rows;
        }

        return backup;
    }

    /// <summary>The backup in an export file, or null when the file has none (an older export, or a task file).</summary>
    public static JsonObject? Read(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["household"] as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the rows into a household. A row whose id is already here is left as it is. Columns
    /// this version doesn't know are ignored, so an export from a newer version still restores.
    /// </summary>
    public static HouseholdRestore Restore(SqliteConnection connection, JsonObject backup, Guid householdId, bool dryRun = false)
    {
        var added = new Dictionary<string, int>();
        var already = 0;
        using var transaction = connection.BeginTransaction();
        foreach (var (table, name) in Tables)
        {
            if (backup[table] is not JsonArray rows)
            {
                continue;
            }

            var known = Columns(connection, table);
            foreach (var node in rows)
            {
                if (node is not JsonObject row || row["id"]?.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }

                var values = row.Where(pair => known.Contains(pair.Key) && pair.Key != "household_id")
                    .ToDictionary(pair => pair.Key, pair => Value(pair.Value));
                values["household_id"] = householdId.ToString();
                if (values.TryGetValue("tier", out var tier) && Convert.ToInt32(tier, System.Globalization.CultureInfo.InvariantCulture) > (int)PrivacyTier.Household)
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                var columns = values.Keys.ToList();
                command.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(column => "$" + column))}) ON CONFLICT (id) DO NOTHING";
                foreach (var column in columns)
                {
                    command.Parameters.AddWithValue("$" + column, values[column] ?? DBNull.Value);
                }

                try
                {
                    if (command.ExecuteNonQuery() == 1)
                    {
                        added[name] = added.GetValueOrDefault(name) + 1;
                    }
                    else
                    {
                        already++;
                    }
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode is 19)
                {
                    // A row missing what it needs (a log entry whose thing isn't in the file) is left out.
                }
            }
        }

        if (dryRun)
        {
            transaction.Rollback();
        }
        else
        {
            transaction.Commit();
        }

        return new HouseholdRestore(added, already);
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static object? Value(JsonNode? node) => node?.GetValueKind() switch
    {
        null or JsonValueKind.Null => null,
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.AsValue().TryGetValue<long>(out var whole) ? whole : node.GetValue<double>(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => null,
    };
}
