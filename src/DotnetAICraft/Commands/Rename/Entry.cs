using DotnetAICraft.Commands.Shared;
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
        string? idleTimeout)
    {
        Validation.ValidateCliModeArgs(file, line, col, symbol);

        var @params = symbol is not null
            ? (object)new { symbol, to, dryRun }
            : new { file = file!.FullName, line = line!.Value, col = col!.Value, to, dryRun };

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, @params, idleTimeout);
            if (res is null)
                return;

            if (CommandHelpers.TryHandleError(res))
                return;

            JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
        }
    }
}
