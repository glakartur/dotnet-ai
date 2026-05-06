using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class CallersCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var fileOpt = new Option<FileInfo?>("--file") { Description = "Source file containing the symbol" };
        var lineOpt = new Option<int?>("--line") { Description = "1-based line number" };
        var colOpt = new Option<int?>("--col") { Description = "1-based column number" };
        var symbolOpt = new Option<string?>("--symbol") { Description = "Fully-qualified method name" };

        var directionOpt = new Option<string>("--direction")
        {
            Description = $"Call graph direction: {DaemonServer.CallGraphDirectionAcceptedValues}",
            DefaultValueFactory = _ => DaemonServer.CallGraphDefaultDirection
        };

        var depthOpt = new Option<int>("--depth")
        {
            Description = $"Call graph traversal depth (min: 1, default: {DaemonServer.CallGraphDefaultDepth})",
            DefaultValueFactory = _ => DaemonServer.CallGraphDefaultDepth
        };

        var cmd = new Command("callers", "Find method callers or callees (call graph)")
        {
            solutionOption,
            fileOpt,
            lineOpt,
            colOpt,
            symbolOpt,
            directionOpt,
            depthOpt,
            idleTimeoutOption
        };

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var file = parseResult.GetValue(fileOpt);
            var line = parseResult.GetValue(lineOpt);
            var col = parseResult.GetValue(colOpt);
            var symbol = parseResult.GetValue(symbolOpt);
            var direction = parseResult.GetRequiredValue(directionOpt);
            var depth = parseResult.GetRequiredValue(depthOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            ValidateArgs(file, line, col, symbol);

            if (!DaemonServer.TryParseCallGraphDirection(direction, out var normalizedDirection))
            {
                JsonOutput.WriteError(
                    "INVALID_PARAMS",
                    "Invalid 'direction' parameter.",
                    new { acceptedValues = DaemonServer.CallGraphDirectionAcceptedValues });
                return;
            }

            if (!DaemonServer.TryNormalizeCallGraphDepth(depth, out var normalizedDepth, out var depthError))
            {
                JsonOutput.WriteError(
                    depthError?.Code ?? "INVALID_PARAMS",
                    depthError?.Message ?? "Invalid call graph depth parameter.",
                    depthError?.Details);
                return;
            }

            var @params = !string.IsNullOrWhiteSpace(symbol)
                ? (object)new { symbol = symbol.Trim(), direction = normalizedDirection, depth = normalizedDepth }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value, direction = normalizedDirection, depth = normalizedDepth };

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("callers", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }

    private static void ValidateArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) && (file is null || line is null || col is null))
        {
            throw new ArgumentException(
                "Provide either --symbol OR all of --file --line --col");
        }
    }
}

