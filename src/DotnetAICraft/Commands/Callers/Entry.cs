using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Callers;

internal static class Entry
{
    private const string CommandName = "callers";

    internal static async Task ExecuteAsync(
        string solutionPath,
        FileInfo? file,
        int? line,
        int? col,
        string? symbol,
        string direction,
        int depth,
        string? idleTimeout)
    {
        Validation.ValidateCliModeArgs(file, line, col, symbol);

        if (!Validation.TryNormalizeDirection(direction, out var normalizedDirection, out var directionError))
        {
            JsonOutput.WriteError(directionError!.Code, directionError.Message, directionError.Details);
            return;
        }

        if (!Validation.TryNormalizeDepth(depth, out var normalizedDepth, out var depthError))
        {
            JsonOutput.WriteError(depthError!.Code, depthError.Message, depthError.Details);
            return;
        }

        var @params = !string.IsNullOrWhiteSpace(symbol)
            ? (object)new { symbol = symbol.Trim(), direction = normalizedDirection, depth = normalizedDepth }
            : new { file = file!.FullName, line = line!.Value, col = col!.Value, direction = normalizedDirection, depth = normalizedDepth };

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
