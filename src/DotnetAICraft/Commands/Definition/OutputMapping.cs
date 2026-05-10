using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Definition;

internal static class OutputMapping
{
    internal static DefinitionResult Map(ISymbol symbol)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);

        string? file = null;
        int? line = null;
        int? col = null;

        if (sourceLocation is not null)
        {
            var sourcePosition = sourceLocation.GetFileLineCol();
            file = sourcePosition.File;
            line = sourcePosition.Line;
            col = sourcePosition.Col;
        }

        return new DefinitionResult(
            FullName: symbol.ToDisplayString(),
            Kind: symbol.GetKindName(),
            File: file,
            Line: line,
            Col: col,
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace: symbol.ContainingNamespace?.ToDisplayString());
    }
}
