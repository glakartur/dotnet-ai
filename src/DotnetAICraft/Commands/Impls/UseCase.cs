using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetAICraft.Commands.Impls;

internal static class UseCase
{
    internal static async Task<IReadOnlyList<SymbolResult>> ResolveAsync(
        Solution solution,
        string symbol,
        CancellationToken ct = default)
    {
        Validation.ValidateDaemonArgs(symbol);

        var resolved = await SymbolResolver.FromFullNameAsync(solution, symbol, ct);
        var impls = resolved is INamedTypeSymbol namedType
            ? await SymbolFinder.FindImplementationsAsync(namedType, solution, transitive: false, projects: null, ct)
            : await SymbolFinder.FindImplementationsAsync(resolved, solution, projects: null, ct);

        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;
        return impls.Select(s => OutputMapping.Map(s, solutionDir)).ToList();
    }
}
