using System.Text.RegularExpressions;

namespace EtchaSketch.Data.Storage.KeyStores;

/// <summary>Key names are fixed app constants, so anything else is a bug, not user input.</summary>
public static partial class KeyNames
{
    /// <summary>Throws unless the name is 1 to 64 lowercase letters, digits, dots or dashes.</summary>
    public static string Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ValidName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid key name.", nameof(name));
        }

        return name;
    }

    [GeneratedRegex("^[a-z0-9.-]{1,64}$")]
    private static partial Regex ValidName();
}
