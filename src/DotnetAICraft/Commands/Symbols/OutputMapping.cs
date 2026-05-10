using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Symbols;

internal static class OutputMapping
{
    internal static Task<SymbolsResultPage> MapAsync(
        Solution solution,
        string pattern,
        string normalizedKind,
        int normalizedLimit,
        int normalizedOffset,
        CancellationToken ct = default)
        => DaemonServer.CollectSymbolsAsync(solution, pattern, normalizedKind, normalizedLimit, normalizedOffset, ct);
}
