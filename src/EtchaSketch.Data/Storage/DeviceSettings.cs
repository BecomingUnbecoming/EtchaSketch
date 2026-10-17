using Microsoft.Data.Sqlite;

namespace EtchaSketch.Data.Storage;

/// <summary>Small settings that belong to this device only and never sync, like which sync server it uses.</summary>
public sealed class DeviceSettings(SqliteConnection connection)
{
    public string? Get(string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM device_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO device_settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
