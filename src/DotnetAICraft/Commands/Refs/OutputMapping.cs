using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetAICraft.Commands.Refs;

internal static class OutputMapping
{
    internal static ReferenceResult Map(ReferenceLocation location, string solutionDir)
    {
        var (file, line, col) = location.Location.GetFileLineColRelative(solutionDir);
        return new ReferenceResult(file, line, col, location.Location.GetContextLine());
    }
}
