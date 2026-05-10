using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetAICraft.Commands.Refs;

internal static class UseCase
{
    internal static async Task<IReadOnlyList<ReferenceResult>> ResolveAsync(
        Solution solution,
        string? symbol,
        string? file,
        int? line,
        int? col,
        CancellationToken ct = default)
    {
        ISymbol resolved = symbol is not null
            ? await SymbolResolver.FromFullNameAsync(solution, symbol, ct)
            : await SymbolResolver.FromLocationAsync(solution, file!, line!.Value, col!.Value, ct);

        var refs = await SymbolFinder.FindReferencesAsync(resolved, solution, ct);

        return refs
            .SelectMany(reference => reference.Locations)
            .Select(OutputMapping.Map)
            .ToList();
    }
}
