using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Unused;

internal static class Entry
{
    private const string CommandName = "unused";

    internal static async Task ExecuteAsync(
        string solutionPath,
        string kind,
        string? project,
        bool publicOnly,
        bool includeGenerated,
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        if (!Validation.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
        {
            CommandHelpers.WriteError(format, kindError!.Code, kindError.Message, kindError.Details);
            return;
        }

        var res = await CommandHelpers.SendWithRetryOrWriteErrorAsync(
            solutionPath,
            CommandName,
            new
            {
                kind = normalizedKind,
                project,
                publicOnly,
                includeGenerated
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
            var summary = JsonOutput.Deserialize<UnusedScanSummary>((JsonElement)res.Result!);
            if (summary is not null)
                TextOutput.WriteUnused(summary, solutionPath);
        }
    }
}
