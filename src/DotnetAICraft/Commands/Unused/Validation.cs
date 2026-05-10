using DotnetAICraft.Daemon;
using DotnetAICraft.Models;

namespace DotnetAICraft.Commands.Unused;

internal static class Validation
{
    internal static bool TryNormalizeKind(string? raw, out string normalizedKind, out ErrorInfo? error)
    {
        if (DaemonServer.TryParseUnusedKind(raw, out normalizedKind))
        {
            error = null;
            return true;
        }

        error = new ErrorInfo(
            "INVALID_PARAMS",
            "Invalid 'kind' parameter.",
            new { acceptedValues = DaemonServer.UnusedKindAcceptedValues });
        return false;
    }
}
