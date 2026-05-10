using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Unused;

internal static class UseCase
{
    internal static async Task<UnusedScanSummary> ResolveAsync(
        Solution solution,
        string? kind,
        string? project,
        bool publicOnly,
        bool includeGenerated,
        CancellationToken ct = default)
    {
        if (!Validation.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
            throw new DaemonValidationException(kindError!);

        return await OutputMapping.MapAsync(solution, normalizedKind, project, publicOnly, includeGenerated, ct);
    }
}
