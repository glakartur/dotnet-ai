using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetAICraft.Commands.Refs;

internal static class OutputMapping
{
    internal static ReferenceResult Map(ReferenceLocation location)
    {
        var (file, line, col) = location.Location.GetFileLineCol();
        return new ReferenceResult(file, line, col, location.Location.GetContextLine());
    }
}
