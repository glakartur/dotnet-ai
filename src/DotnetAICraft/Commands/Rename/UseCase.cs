using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;

namespace DotnetAICraft.Commands.Rename;

internal static class UseCase
{
    internal static async Task<RenameResult> ResolveAsync(
        Solution solution,
        string to,
        bool dryRun,
        string? symbol,
        string? file,
        int? line,
        int? col,
        CancellationToken ct = default)
    {
        var symbolName = symbol;
        ISymbol resolved = symbolName is not null
            ? await SymbolResolver.FromFullNameAsync(solution, symbolName, ct)
            : await SymbolResolver.FromLocationAsync(solution, file!, line!.Value, col!.Value, ct);

        var newSolution = await Renamer.RenameSymbolAsync(
            solution, resolved, new SymbolRenameOptions(), to, ct);

        return await OutputMapping.MapAsync(solution, newSolution, resolved, to, dryRun, ct);
    }
}
