using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DotnetAi.Roslyn;

public static class SymbolResolver
{
    /// <summary>
    /// Resolves a symbol by source file location (file + 1-based line + 1-based col).
    /// This is the most reliable way for an agent to identify a symbol after reading source.
    /// </summary>
    public static async Task<ISymbol> FromLocationAsync(
        Solution solution,
        string filePath,
        int line,
        int col,
        CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        var docId = solution.GetDocumentIdsWithFilePath(normalizedPath).FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"File not found in solution: {filePath}\n" +
                $"Tip: make sure the path is absolute or relative to the solution directory.");

        var document = solution.GetDocument(docId)!;
        var sourceText = await document.GetTextAsync(ct);

        // Convert 1-based to 0-based
        var lineIndex = line - 1;
        var colIndex  = col  - 1;

        if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(line),
                $"Line {line} is out of range (file has {sourceText.Lines.Count} lines).");

        var textLine   = sourceText.Lines[lineIndex];
        var position   = textLine.Start + Math.Min(colIndex, textLine.End - textLine.Start);

        var semanticModel = await document.GetSemanticModelAsync(ct)
            ?? throw new InvalidOperationException("Could not get semantic model for document.");

        var root = await document.GetSyntaxRootAsync(ct)!;
        var node = root!.FindToken(position).Parent;

        // Walk up to find the nearest named syntax node
        while (node is not null)
        {
            var symbol = semanticModel.GetSymbolInfo(node, ct).Symbol
                      ?? semanticModel.GetDeclaredSymbol(node, ct);

            if (symbol is not null)
                return symbol;

            node = node.Parent;
        }

        throw new InvalidOperationException(
            $"No symbol found at {filePath}:{line}:{col}.\n" +
            $"Tip: point to the symbol identifier, not whitespace or punctuation.");
    }

    /// <summary>
    /// Resolves a symbol by its fully-qualified name (e.g. "MyApp.Services.OrderService.Process").
    /// Slower than location-based — searches all projects.
    /// </summary>
    public static async Task<ISymbol> FromFullNameAsync(
        Solution solution,
        string fullName,
        CancellationToken ct = default)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Try exact match first
            var symbols = compilation.GetSymbolsWithName(
                name => fullName.EndsWith(name, StringComparison.Ordinal),
                SymbolFilter.All, ct);

            var match = symbols.FirstOrDefault(s =>
                s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                 .Replace("global::", "")
                 .Equals(fullName, StringComparison.Ordinal));

            if (match is not null)
                return match;
        }

        throw new InvalidOperationException(
            $"Symbol '{fullName}' not found in any project in the solution.\n" +
            $"Tip: use the fully qualified name, e.g. 'MyApp.Services.OrderService.ProcessOrder'.");
    }

    /// <summary>
    /// Search symbols by pattern (supports * and ? wildcards).
    /// </summary>
    public static async IAsyncEnumerable<ISymbol> SearchAsync(
        Solution solution,
        string pattern,
        SymbolFilter filter = SymbolFilter.All,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            var symbols = compilation.GetSymbolsWithName(
                name => MatchesPattern(name, pattern), filter, ct);

            foreach (var symbol in symbols)
            {
                var key = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (seen.Add(key))
                    yield return symbol;
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        // Simple glob matching
        return GlobMatch(pattern.ToLowerInvariant(), name.ToLowerInvariant());
    }

    private static bool GlobMatch(string pattern, string input)
    {
        int pi = 0, si = 0, starPi = -1, starSi = 0;
        while (si < input.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == input[si]))
            { pi++; si++; }
            else if (pi < pattern.Length && pattern[pi] == '*')
            { starPi = pi++; starSi = si; }
            else if (starPi >= 0)
            { pi = starPi + 1; si = ++starSi; }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }
}
