using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EtchaSketch.Data.Storage.KeyStores;

/// <summary>
/// Keeps keys in the desktop's Secret Service (GNOME Keyring or KWallet) through the
/// <c>secret-tool</c> command. The key goes over stdin, never on the command line, where other
/// programs could read it.
/// </summary>
/// <param name="collection">The keyring to store new keys in. Null means your default keyring.</param>
[SupportedOSPlatform("linux")]
public sealed class LinuxKeyStore(string? collection = null) : IKeyStore
{
    private const string Application = "etchasketch";

    public byte[]? Load(string name)
    {
        var result = Run(input: null, "lookup", "application", Application, "name", KeyNames.Validate(name));

        // secret-tool exits 1 with no message when nothing matches.
        if (result.ExitCode != 0 && result.Error.Length == 0)
        {
            return null;
        }

        EnsureSucceeded(result, "read a key from");
        return Convert.FromBase64String(result.Output.Trim());
    }

    public void Save(string name, byte[] key)
    {
        List<string> arguments = ["store", $"--label=EtchaSketch {KeyNames.Validate(name)}"];
        if (collection is not null)
        {
            arguments.Add($"--collection={collection}");
        }

        arguments.AddRange(["application", Application, "name", name]);
        EnsureSucceeded(Run(Convert.ToBase64String(key), [.. arguments]), "save a key to");
    }

    public void Delete(string name)
    {
        var result = Run(input: null, "clear", "application", Application, "name", KeyNames.Validate(name));
        if (result.ExitCode != 0 && result.Error.Length > 0)
        {
            EnsureSucceeded(result, "delete a key from");
        }
    }

    private static void EnsureSucceeded(ToolResult result, string action)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Couldn't {action} the Secret Service: {result.Error.Trim()}");
        }
    }

    private static ToolResult Run(string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "EtchaSketch needs 'secret-tool' to keep its keys safe. Install it (Debian and Ubuntu: libsecret-tools; Fedora: libsecret).",
                exception);
        }

        using (process)
        {
            if (input is not null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new ToolResult(process.ExitCode, output, error.Result);
        }
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);
}
