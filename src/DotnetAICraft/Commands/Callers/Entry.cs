using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
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
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        Validation.ValidateCliModeArgs(file, line, col, symbol);

        if (!Validation.TryNormalizeDirection(direction, out var normalizedDirection, out var directionError))
        {
            CommandHelpers.WriteError(format, directionError!.Code, directionError.Message, directionError.Details);
            return;
        }

        if (!Validation.TryNormalizeDepth(depth, out var normalizedDepth, out var depthError))
        {
            CommandHelpers.WriteError(format, depthError!.Code, depthError.Message, depthError.Details);
            return;
        }

        var @params = !string.IsNullOrWhiteSpace(symbol)
            ? (object)new { symbol = symbol.Trim(), direction = normalizedDirection, depth = normalizedDepth }
            : new { file = file!.FullName, line = line!.Value, col = col!.Value, direction = normalizedDirection, depth = normalizedDepth };

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

            var solutionDir = Path.GetDirectoryName(solutionPath) ?? string.Empty;
            if (format == OutputFormat.Json)
            {
                JsonOutput.WriteWithSolutionRoot(solutionDir, CommandHelpers.GetDataOrNull(res));
            }
            else
            {
                TextOutput.WriteSolutionRootHeader(solutionDir);
                var target = !string.IsNullOrWhiteSpace(symbol) ? symbol! : $"{file!.FullName}:{line}:{col}";
                var graph = JsonOutput.Deserialize<CallGraphResult>((JsonElement)res.Result!);
                if (graph is not null)
                    TextOutput.WriteCallers(graph, target, solutionPath);
            }
        }
    }
}
