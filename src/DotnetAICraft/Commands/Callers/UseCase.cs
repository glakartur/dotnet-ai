using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Callers;

internal static class UseCase
{
    internal static async Task<object> ResolveAsync(
        Solution solution,
        string? symbol,
        string? file,
        int? line,
        int? col,
        string? direction,
        int? depth,
        CancellationToken ct = default)
    {
        Validation.ValidateDaemonModeArgs(symbol, file, line, col);

        if (!Validation.TryNormalizeDirection(direction, out var normalizedDirection, out var directionError))
            throw new DaemonValidationException(directionError!);

        if (!Validation.TryNormalizeDepth(depth, out var normalizedDepth, out var depthError))
            throw new DaemonValidationException(depthError!);

        var resolved = symbol is not null
            ? await SymbolResolver.FromFullNameAsync(solution, symbol.Trim(), ct)
            : await SymbolResolver.FromLocationAsync(solution, file!, line!.Value, col!.Value, ct);

        return await OutputMapping.MapAsync(solution, resolved, normalizedDirection, normalizedDepth, ct);
    }
}
