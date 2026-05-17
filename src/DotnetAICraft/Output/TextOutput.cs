using System.Globalization;
using System.Text.Json;
using DotnetAICraft.Models;

namespace DotnetAICraft.Output;

public static class TextOutput
{
    private static string OneLine(string? s)
        => s is null ? string.Empty : s.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

    private static string Pluralize(int count, string singular, string plural)
        => count == 1 ? singular : plural;

    // ── Envelope header ──────────────────────────────────────────────────────
    public static void WriteSolutionRootHeader(string absoluteSolutionDir)
    {
        Console.Out.WriteLine($"SolutionRoot: {absoluteSolutionDir}");
        Console.Out.WriteLine();
    }

    // ── Refs ─────────────────────────────────────────────────────────────────
    public static void WriteRefs(IReadOnlyList<ReferenceResult> items, string target, string solution)
    {
        var word = Pluralize(items.Count, "reference", "references");
        Console.Out.WriteLine($"{items.Count} {word} to {target} in {solution}");
        if (items.Count == 0) return;
        Console.Out.WriteLine();
        foreach (var r in items)
            Console.Out.WriteLine($"{r.File}:{r.Line}:{r.Col}: {OneLine(r.Context)}");
    }

    // ── Impls ────────────────────────────────────────────────────────────────
    public static void WriteImpls(IReadOnlyList<SymbolResult> items, string target, string solution)
    {
        var word = Pluralize(items.Count, "implementation", "implementations");
        Console.Out.WriteLine($"{items.Count} {word} of {target} in {solution}");
        if (items.Count == 0) return;
        Console.Out.WriteLine();
        foreach (var s in items)
            Console.Out.WriteLine($"{s.File}:{s.Line}:{s.Col}: {s.Kind} {s.FullName}");
    }

    // ── Callers ──────────────────────────────────────────────────────────────
    public static void WriteCallers(CallGraphResult result, string target, string solution)
    {
        var nodeById = result.Nodes.ToDictionary(n => n.Id);
        // Number of "caller" rows = number of edges
        var count = result.Edges.Count;
        var word = Pluralize(count, "caller", "callers");
        Console.Out.WriteLine($"{count} {word} of {target} in {solution}");
        if (count == 0) return;
        Console.Out.WriteLine();
        foreach (var e in result.Edges)
        {
            // Render the "other side" of the edge depending on direction.
            // For incoming (callers): show From; for outgoing (callees): show To.
            var otherId = string.Equals(result.Direction, "outgoing", StringComparison.OrdinalIgnoreCase) ? e.To : e.From;
            if (!nodeById.TryGetValue(otherId, out var node))
                continue;
            Console.Out.WriteLine($"{node.File}:{node.Line}:{node.Col}: {node.Kind} {node.FullName}");
        }
    }

    // ── Symbols ──────────────────────────────────────────────────────────────
    public static void WriteSymbols(SymbolsResultPage page, string pattern, string solution)
    {
        var word = Pluralize(page.Items.Count, "symbol", "symbols");
        var header = $"{page.Items.Count} {word} matching {pattern} in {solution}";
        if (page.HasMore)
            header += " (more available — use --offset to continue)";
        Console.Out.WriteLine(header);
        if (page.Items.Count == 0) return;
        Console.Out.WriteLine();
        foreach (var s in page.Items)
            Console.Out.WriteLine($"{s.File}:{s.Line}:{s.Col}: {s.Kind} {s.FullName}");
    }

    // ── Unused ───────────────────────────────────────────────────────────────
    public static void WriteUnused(UnusedScanSummary summary, string solution)
    {
        var word = Pluralize(summary.Items.Count, "candidate", "candidates");
        var publicOnly = summary.PublicOnly ? "true" : "false";
        var includeGenerated = summary.IncludeGenerated ? "true" : "false";
        Console.Out.WriteLine(
            $"{summary.Items.Count} unused {summary.Kind} {word} (scanned {summary.Scanned}, publicOnly={publicOnly}, includeGenerated={includeGenerated}) in {solution}");
        if (summary.Items.Count == 0) return;
        Console.Out.WriteLine();
        foreach (var u in summary.Items)
        {
            var conf = u.Confidence.ToString("0.##", CultureInfo.InvariantCulture);
            Console.Out.WriteLine($"{u.File}:{u.Line}:{u.Col}: {u.Kind} {u.Symbol} [confidence={conf}] ({u.Reason})");
        }
    }

    // ── Definition ───────────────────────────────────────────────────────────
    public static void WriteDefinition(DefinitionResult def, string solution)
    {
        Console.Out.WriteLine(def.FullName);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"Kind: {def.Kind}");
        if (def.File is not null && def.Line is not null && def.Col is not null)
            Console.Out.WriteLine($"Location: {def.File}:{def.Line}:{def.Col}");
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────
    public static void WriteDiagnostics(IReadOnlyList<DiagnosticResult> items, string solution)
    {
        int errors = 0, warnings = 0;
        foreach (var d in items)
        {
            if (string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase)) errors++;
            else if (string.Equals(d.Severity, "warning", StringComparison.OrdinalIgnoreCase)) warnings++;
        }
        Console.Out.WriteLine($"{errors} errors, {warnings} warnings");
        if (items.Count == 0) return;
        Console.Out.WriteLine();
        foreach (var d in items)
        {
            var sev = d.Severity?.ToLowerInvariant() ?? string.Empty;
            if (d.File is not null && d.Line is not null && d.Col is not null)
                Console.Out.WriteLine($"{sev} {d.File}:{d.Line}:{d.Col} [{d.Id}]: {OneLine(d.Message)}");
            else
                Console.Out.WriteLine($"{sev} {d.Project} [{d.Id}]: {OneLine(d.Message)}");
        }
    }

    // ── Rename ───────────────────────────────────────────────────────────────
    public static void WriteRename(RenameResult result, string solution)
    {
        var word = Pluralize(result.Changes.Count, "change", "changes");
        var status = result.Applied ? "applied" : "dry-run";
        Console.Out.WriteLine(
            $"{result.Changes.Count} {word} for {result.Symbol} -> {result.NewName} ({status}) in {solution}");
        if (result.Changes.Count == 0) return;
        Console.Out.WriteLine();
        foreach (var c in result.Changes)
            Console.Out.WriteLine($"{c.File}:{c.Line}:{c.Col}: {c.OldText} -> {c.NewText}");
    }

    // ── Server status ────────────────────────────────────────────────────────
    public static void WriteServerStatus(DaemonStatus status)
    {
        Console.Out.WriteLine($"{status.SolutionPath} [{status.LoadState}]");
        Console.Out.WriteLine($"Running: {(status.Running ? "true" : "false")}");
        Console.Out.WriteLine($"Projects: {status.Projects}");
        Console.Out.WriteLine($"Documents: {status.Documents}");
        Console.Out.WriteLine($"LoadedAt: {status.LoadedAt:O}");
        Console.Out.WriteLine($"Uptime: {status.Uptime}");
        if (status.LastLoadAttemptAt is not null)
            Console.Out.WriteLine($"LastLoadAttemptAt: {status.LastLoadAttemptAt:O}");
        if (status.LastLoadErrorCode is not null || status.LastLoadErrorMessage is not null)
            Console.Out.WriteLine($"LastLoadError: {status.LastLoadErrorCode}: {status.LastLoadErrorMessage}");
    }

    // ── Error ────────────────────────────────────────────────────────────────
    public static void WriteError(string code, string message, object? details)
    {
        Console.Out.WriteLine($"error {code}: {message}");
        if (details is null) return;

        // Try to extract a "hint" property first, regardless of carrier shape.
        var json = JsonOutput.Serialize(details);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("hint", out var hint) && hint.ValueKind == JsonValueKind.String)
                {
                    Console.Out.WriteLine($"hint: {hint.GetString()}");
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "hint", StringComparison.Ordinal))
                        continue;
                    string value = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "null",
                        _ => prop.Value.GetRawText()
                    };
                    Console.Out.WriteLine($"  {prop.Name}: {value}");
                }
            }
        }
        catch
        {
            // ignore; fall back silently
        }
    }
}
