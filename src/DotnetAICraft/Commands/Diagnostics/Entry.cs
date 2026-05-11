using DotnetAICraft.Commands.Shared;
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
        string acceptedSeverities)
    {
        if (!Validation.TryNormalizeSeverity(severity, acceptedSeverities, out var normalizedSeverity, out var severityError))
        {
            JsonOutput.WriteError(severityError!.Code, severityError.Message, severityError.Details);
            return;
        }

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, new
            {
                severity = normalizedSeverity,
                project,
                file = file?.FullName
            });

            if (res is null)
                return;

            if (!CommandHelpers.TryHandleError(res))
                JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
        }
    }
}
