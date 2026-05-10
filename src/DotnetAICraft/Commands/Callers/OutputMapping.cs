using DotnetAICraft.Daemon;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Callers;

internal static class OutputMapping
{
    internal static async Task<object> MapAsync(
        Solution solution,
        ISymbol symbol,
        string normalizedDirection,
        int normalizedDepth,
        CancellationToken ct)
    {
        if (string.Equals(normalizedDirection, DaemonServer.CallGraphDefaultDirection, StringComparison.Ordinal) &&
            normalizedDepth == DaemonServer.CallGraphDefaultDepth)
        {
            return await DaemonServer.CollectIncomingCallersAsync(solution, symbol, ct);
        }

        return await DaemonServer.CollectCallGraphAsync(solution, symbol, normalizedDirection, normalizedDepth, ct);
    }
}
