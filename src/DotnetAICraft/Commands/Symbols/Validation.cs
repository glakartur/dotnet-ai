using DotnetAICraft.Daemon;
using DotnetAICraft.Models;

namespace DotnetAICraft.Commands.Symbols;

internal static class Validation
{
    internal static bool TryNormalizeKind(string? raw, out string normalizedKind, out ErrorInfo? error)
    {
        if (DaemonServer.TryParseSymbolsKind(raw, out _, out _, out normalizedKind))
        {
            error = null;
            return true;
        }

        error = new ErrorInfo(
            "INVALID_PARAMS",
            "Invalid 'kind' parameter.",
            new { acceptedValues = DaemonServer.SymbolsKindAcceptedValues });
        return false;
    }

    internal static bool TryNormalizePagination(
        int? limit,
        int? offset,
        out int normalizedLimit,
        out int normalizedOffset,
        out ErrorInfo? error)
        => DaemonServer.TryNormalizeSymbolsPagination(limit, offset, out normalizedLimit, out normalizedOffset, out error);
}
