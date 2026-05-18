using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Diagnostics;

internal static class Entry
{
    private const string CommandName = "diagnostics";

    internal static async Task ExecuteAsync(
        string solutionPath,
        string severity,
        string? project,
        FileInfo? file,
        string? idleTimeout,
        string acceptedSeverities,
        OutputFormat format = OutputFormat.Text)
    {
        if (!Validation.TryNormalizeSeverity(severity, acceptedSeverities, out var normalizedSeverity, out var severityError))
        {
            CommandHelpers.WriteError(format, severityError!.Code, severityError.Message, severityError.Details);
            return;
        }

        var res = await CommandHelpers.SendWithRetryOrWriteErrorAsync(
            solutionPath,
            CommandName,
            new
            {
                severity = normalizedSeverity,
                project,
                file = file?.FullName
            },
            idleTimeout,
            format: format);

        if (res is null)
            return;

        if (CommandHelpers.TryHandleError(res, format))
            return;

        var solutionDir = Path.GetDirectoryName(solutionPath) ?? string.Empty;
        if (format == OutputFormat.Json)
        {
            JsonOutput.WriteWithSolutionRoot(solutionDir, CommandHelpers.GetDataOrNull(res));
        }
        else
        {
            TextOutput.WriteSolutionRootHeader(solutionDir);
            var items = JsonOutput.Deserialize<IReadOnlyList<DiagnosticResult>>((JsonElement)res.Result!) ?? Array.Empty<DiagnosticResult>();
            TextOutput.WriteDiagnostics(items, solutionPath);
        }
    }
}
