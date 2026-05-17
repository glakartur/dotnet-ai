using DotnetAICraft.Models;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Rename;

internal static class OutputMapping
{
    internal static async Task<RenameResult> MapAsync(
        Solution oldSolution,
        Solution newSolution,
        ISymbol resolved,
        string newName,
        bool dryRun,
        CancellationToken ct)
    {
        var solutionChanges = newSolution.GetChanges(oldSolution);
        var changes = new List<RenameChange>();
        var solutionDir = Path.GetDirectoryName(oldSolution.FilePath) ?? string.Empty;

        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        foreach (var docChange in projectChanges.GetChangedDocuments())
        {
            var oldDoc = oldSolution.GetDocument(docChange)!;
            var updatedDoc = newSolution.GetDocument(docChange)!;
            var oldText = await oldDoc.GetTextAsync(ct);
            var updatedText = await updatedDoc.GetTextAsync(ct);

            foreach (var change in updatedText.GetTextChanges(oldText))
            {
                var linePos = oldText.Lines.GetLinePosition(change.Span.Start);
                var oldTextSegment = oldText.GetSubText(change.Span).ToString();

                changes.Add(new RenameChange(
                    File: PathFormatter.ToRelative(oldDoc.FilePath, solutionDir) ?? string.Empty,
                    Line: linePos.Line + 1,
                    Col: linePos.Character + 1,
                    OldText: oldTextSegment,
                    NewText: change.NewText ?? string.Empty));
            }
        }

        return new RenameResult(
            Symbol: resolved.ToDisplayString(),
            NewName: newName,
            Applied: !dryRun,
            DryRun: dryRun,
            Changes: changes);
    }
}
