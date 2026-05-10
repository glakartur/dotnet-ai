using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Server;

internal static class OutputMapping
{
    internal static void WriteError(ErrorInfo? error, string fallbackCode = "UNKNOWN_ERROR", string fallbackMessage = "Unknown daemon error.")
    {
        JsonOutput.WriteError(
            error?.Code ?? fallbackCode,
            error?.Message ?? fallbackMessage,
            error?.Details);
    }

    internal static void Write(object? value)
        => JsonOutput.Write(value);
}
