using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Diagnostics;

internal static class OutputMapping
{
    internal static Task<IReadOnlyList<DiagnosticResult>> MapAsync(
        Solution solution,
        DiagnosticSeverity? severityFilter,
        string? project,
        string? file,
        CancellationToken ct = default)
        => DaemonServer.CollectDiagnosticsAsync(solution, severityFilter, project, file, ct);
}
