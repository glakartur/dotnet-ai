using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Symbols;

internal static class UseCase
{
    internal static async Task<SymbolsResultPage> ResolveAsync(
        Solution solution,
        string pattern,
        string kind,
        int? limit,
        int? offset,
        CancellationToken ct = default)
    {
        if (!Validation.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
            throw new DaemonValidationException(kindError!);

        if (!Validation.TryNormalizePagination(limit, offset, out var normalizedLimit, out var normalizedOffset, out var paginationError))
            throw new DaemonValidationException(paginationError!);

        return await OutputMapping.MapAsync(solution, pattern, normalizedKind, normalizedLimit, normalizedOffset, ct);
    }
}
