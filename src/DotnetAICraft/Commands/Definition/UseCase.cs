using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Definition;

internal static class UseCase
{
    internal static async Task<DefinitionResult> ResolveAsync(
        Solution solution,
        string? symbol,
        string? file,
        int? line,
        int? col,
        CancellationToken ct = default)
    {
        Validation.ValidateDaemonArgs(symbol, file, line, col);

        var hasSymbol = !string.IsNullOrWhiteSpace(symbol);

        ISymbol resolved = hasSymbol
            ? await SymbolResolver.FromFullNameAsync(solution, symbol!.Trim(), ct)
            : await SymbolResolver.FromLocationAsync(solution, file!, line!.Value, col!.Value, ct);

        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;
        return OutputMapping.Map(resolved, solutionDir);
    }
}
