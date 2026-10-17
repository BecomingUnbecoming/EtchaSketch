using System;

namespace EtchaSketch.UI.ViewModels;

internal static class Messages
{
    /// <summary>The message from a validation error, without .NET's "(Parameter 'x')" suffix.</summary>
    public static string Friendly(ArgumentException exception) =>
        exception.ParamName is null
            ? exception.Message
            : exception.Message.Replace($" (Parameter '{exception.ParamName}')", "", StringComparison.Ordinal);
}
