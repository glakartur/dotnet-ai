using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;

namespace DotnetAICraft.Commands.Diagnostics;

internal static class UseCase
{
    internal static async Task<IReadOnlyList<DiagnosticResult>> ResolveAsync(
        Solution solution,
        string? severity,
        string? project,
        string? file,
        CancellationToken ct = default)
    {
        if (!DaemonServer.TryParseDiagnosticsSeverity(severity, out var severityFilter, out _))
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_PARAMS",
                "Invalid 'severity' parameter.",
                new { acceptedValues = "all | error | warning | info | hidden" }));
        }

        return await OutputMapping.MapAsync(solution, severityFilter, project, file, ct);
    }
}
