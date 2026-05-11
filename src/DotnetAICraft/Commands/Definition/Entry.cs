using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Definition;

internal static class Entry
{
    private const string CommandName = "definition";

    internal static async Task ExecuteAsync(
        string solutionPath,
        FileInfo? file,
        int? line,
        int? col,
        string? symbol,
        string? idleTimeout)
    {
        Validation.ValidateCliArgs(file, line, col, symbol);

        var @params = !string.IsNullOrWhiteSpace(symbol)
            ? (object)new { symbol = symbol.Trim() }
            : new { file = file!.FullName, line = line!.Value, col = col!.Value };

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, @params);
            if (res is null)
                return;

            if (CommandHelpers.TryHandleError(res))
                return;

            JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
        }
    }
}
