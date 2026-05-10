using DotnetAICraft.Daemon;
using DotnetAICraft.Models;

namespace DotnetAICraft.Commands.Callers;

internal static class Validation
{
    internal static void ValidateCliModeArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) && (file is null || line is null || col is null))
        {
            throw new ArgumentException(
                "Provide either --symbol OR all of --file --line --col");
        }
    }

    internal static void ValidateDaemonModeArgs(string? symbol, string? file, int? line, int? col)
    {
        if (symbol is null && (string.IsNullOrWhiteSpace(file) || line is null || col is null))
        {
            throw new ArgumentException("Provide either 'symbol' OR all of 'file'+'line'+'col'.");
        }
    }

    internal static bool TryNormalizeDirection(string? raw, out string normalizedDirection, out ErrorInfo? error)
    {
        if (DaemonServer.TryParseCallGraphDirection(raw, out normalizedDirection))
        {
            error = null;
            return true;
        }

        error = new ErrorInfo(
            "INVALID_PARAMS",
            "Invalid 'direction' parameter.",
            new { acceptedValues = DaemonServer.CallGraphDirectionAcceptedValues });
        return false;
    }

    internal static bool TryNormalizeDepth(int? depth, out int normalizedDepth, out ErrorInfo? error)
        => DaemonServer.TryNormalizeCallGraphDepth(depth, out normalizedDepth, out error);
}
