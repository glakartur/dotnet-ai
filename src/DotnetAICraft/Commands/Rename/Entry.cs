using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Rename;

internal static class Entry
{
    private const string CommandName = "rename";

    internal static async Task ExecuteAsync(
        string solutionPath,
        FileInfo? file,
        int? line,
        int? col,
        string? symbol,
        string to,
        bool dryRun,
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        Validation.ValidateCliModeArgs(file, line, col, symbol);

        var @params = symbol is not null
            ? (object)new { symbol, to, dryRun }
            : new { file = file!.FullName, line = line!.Value, col = col!.Value, to, dryRun };

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout, format);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, @params, idleTimeout, format: format);
            if (res is null)
                return;

            if (CommandHelpers.TryHandleError(res, format))
                return;

            if (format == OutputFormat.Json)
            {
                var solutionDir = Path.GetDirectoryName(solutionPath) ?? string.Empty;
                JsonOutput.WriteWithSolutionRoot(solutionDir, CommandHelpers.GetDataOrNull(res));
            }
            else
            {
                var rename = JsonOutput.Deserialize<RenameResult>((JsonElement)res.Result!);
                if (rename is not null)
                    TextOutput.WriteRename(rename, solutionPath);
            }
        }
    }
}
