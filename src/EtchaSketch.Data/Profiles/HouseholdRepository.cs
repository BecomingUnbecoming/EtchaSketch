using System.Globalization;
using EtchaSketch.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Profiles;

/// <summary>Reads and writes households, their members, and which member is using this device.</summary>
public sealed class HouseholdRepository(SqliteConnection connection)
{
    private const string CurrentProfileKey = "current_profile";

    /// <summary>Creates a household whose first member, the person setting it up, is its owner.</summary>
    public Household Create(string name, string ownerDisplayName)
    {
        var householdName = Required(name, nameof(name));
        var ownerName = Required(ownerDisplayName, nameof(ownerDisplayName));
        var id = Guid.NewGuid();

        using var transaction = connection.BeginTransaction();
        Execute(transaction, "INSERT INTO households (id, name, created_at) VALUES ($id, $name, $now)",
            ("$id", id.ToString()), ("$name", householdName), ("$now", Now()));
        var owner = InsertProfile(transaction, id, ownerName, ProfileRole.Owner);
        transaction.Commit();

        return new Household(id, householdName, [owner]);
    }

    public Profile AddMember(Guid householdId, string displayName)
    {
        var name = Required(displayName, nameof(displayName));
        if (Get(householdId) is null)
        {
            throw new ArgumentException($"There is no household {householdId}.", nameof(householdId));
        }

        using var transaction = connection.BeginTransaction();
        var profile = InsertProfile(transaction, householdId, name, ProfileRole.Member);
        transaction.Commit();
        return profile;
    }

    /// <summary>Returns the household with its members in the order they joined, or null.</summary>
    public Household? Get(Guid householdId)
    {
        using var command = Command("SELECT name FROM households WHERE id = $id", ("$id", householdId.ToString()));
        if (command.ExecuteScalar() is not string name)
        {
            return null;
        }

        using var members = Command(
            "SELECT id, display_name, role FROM profiles WHERE household_id = $id ORDER BY rowid",
            ("$id", householdId.ToString()));
        using var reader = members.ExecuteReader();
        var profiles = new List<Profile>();
        while (reader.Read())
        {
            profiles.Add(new Profile(
                Guid.Parse(reader.GetString(0)),
                householdId,
                reader.GetString(1),
                Enum.Parse<ProfileRole>(reader.GetString(2))));
        }

        return new Household(householdId, name, profiles);
    }

    /// <summary>Every household on this device, oldest first.</summary>
    public IReadOnlyList<Household> All()
    {
        var ids = new List<Guid>();
        using (var command = Command("SELECT id FROM households ORDER BY rowid"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        return ids.Select(id => Get(id)!).ToList();
    }

    /// <summary>The household a profile belongs to, or null when there is no such profile.</summary>
    public Household? HouseholdOf(Guid profileId)
    {
        using var command = Command("SELECT household_id FROM profiles WHERE id = $id", ("$id", profileId.ToString()));
        return command.ExecuteScalar() is string householdId ? Get(Guid.Parse(householdId)) : null;
    }

    /// <summary>The member using this device, or null before anyone has picked a profile.</summary>
    public Guid? CurrentProfileId()
    {
        using var command = Command("SELECT value FROM device_settings WHERE key = $key", ("$key", CurrentProfileKey));
        return command.ExecuteScalar() is string value ? Guid.Parse(value) : null;
    }

    public void SetCurrentProfile(Guid profileId)
    {
        using (var exists = Command("SELECT 1 FROM profiles WHERE id = $id", ("$id", profileId.ToString())))
        {
            if (exists.ExecuteScalar() is null)
            {
                throw new ArgumentException($"There is no profile {profileId}.", nameof(profileId));
            }
        }

        using var command = Command(
            "INSERT INTO device_settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", CurrentProfileKey), ("$value", profileId.ToString()));
        command.ExecuteNonQuery();
    }

    private Profile InsertProfile(SqliteTransaction transaction, Guid householdId, string displayName, ProfileRole role)
    {
        var profile = new Profile(Guid.NewGuid(), householdId, displayName, role);
        Execute(transaction,
            "INSERT INTO profiles (id, household_id, display_name, role, created_at) VALUES ($id, $household, $name, $role, $now)",
            ("$id", profile.Id.ToString()), ("$household", householdId.ToString()), ("$name", displayName),
            ("$role", role.ToString()), ("$now", Now()));
        return profile;
    }

    private static string Required(string value, string parameter)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? throw new ArgumentException("A name is required.", parameter)
            : trimmed;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private void Execute(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
