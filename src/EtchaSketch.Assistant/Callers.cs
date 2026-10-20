using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EtchaSketch.Assistant;

/// <summary>
/// Who may use the assistant's API: Home Assistant, Open WebUI, Claude… Each gets its own token,
/// stored only as a hash, so one can be cut off without touching the others.
/// </summary>
public sealed class Callers(string path)
{
    private sealed record Caller(string Name, string Hash);

    /// <summary>Adds a caller (or replaces its token) and returns the new token. It's shown once.</summary>
    public string Add(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40)
        {
            throw new ArgumentException("A caller needs a short name, like home-assistant.", nameof(name));
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var callers = Load().Where(caller => caller.Name != name).Append(new Caller(name, Hash(token))).ToList();
        Save(callers);
        return token;
    }

    public bool Remove(string name)
    {
        var callers = Load();
        var kept = callers.Where(caller => caller.Name != name).ToList();
        Save(kept);
        return kept.Count != callers.Count;
    }

    public IReadOnlyList<string> Names() => Load().Select(caller => caller.Name).ToList();

    /// <summary>The caller a token belongs to, or null.</summary>
    public string? Who(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Convert.FromHexString(Hash(token));
        return Load().FirstOrDefault(caller => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(caller.Hash), hash))?.Name;
    }

    private List<Caller> Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<Caller>>(File.ReadAllText(path)) ?? [] : [];

    private void Save(List<Caller> callers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(callers));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
