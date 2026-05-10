using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Impls;

internal static class OutputMapping
{
    internal static SymbolResult Map(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var (file, line, col) = location is not null ? location.GetFileLineCol() : ("", 0, 0);

        return new SymbolResult(
            Name: symbol.Name,
            FullName: symbol.ToDisplayString(),
            Kind: symbol.GetKindName(),
            File: file,
            Line: line,
            Col: col,
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace: symbol.ContainingNamespace?.ToDisplayString());
    }
}
