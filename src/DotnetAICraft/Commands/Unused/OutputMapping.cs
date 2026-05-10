using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Unused;

internal static class OutputMapping
{
    internal static Task<UnusedScanSummary> MapAsync(
        Solution solution,
        string? kind,
        string? project,
        bool publicOnly,
        bool includeGenerated,
        CancellationToken ct = default)
        => DaemonServer.CollectUnusedAsync(solution, kind, project, publicOnly, includeGenerated, ct);
}
