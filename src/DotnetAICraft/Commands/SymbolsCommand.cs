using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class SymbolsCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var patternOpt = new Option<string>("--pattern")
        {
            Description = "Symbol name pattern (supports * and ? wildcards)",
            Required = true
        };

        var kindOpt = new Option<string>("--kind")
        {
            Description = $"Symbol kind filter: {DaemonServer.SymbolsKindAcceptedValues}",
            DefaultValueFactory = _ => "all"
        };

        var limitOpt = new Option<int>("--limit")
        {
            Description = $"Maximum number of results to return (default: {DaemonServer.SymbolsDefaultLimit}, max: {DaemonServer.SymbolsMaxLimit})",
            DefaultValueFactory = _ => DaemonServer.SymbolsDefaultLimit
        };

        var offsetOpt = new Option<int>("--offset")
        {
            Description = "Number of matching results to skip before collecting output",
            DefaultValueFactory = _ => DaemonServer.SymbolsDefaultOffset
        };

        var cmd = new Command("symbols", "Search symbols by name pattern across the solution")
        {
            solutionOption, patternOpt, kindOpt, limitOpt, offsetOpt, idleTimeoutOption
        };

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var pattern = parseResult.GetRequiredValue(patternOpt);
            var kind = parseResult.GetRequiredValue(kindOpt);
            var limit = parseResult.GetRequiredValue(limitOpt);
            var offset = parseResult.GetRequiredValue(offsetOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            if (!DaemonServer.TryParseSymbolsKind(kind, out _, out _, out var normalizedKind))
            {
                JsonOutput.WriteError(
                    "INVALID_PARAMS",
                    "Invalid 'kind' parameter.",
                    new { acceptedValues = DaemonServer.SymbolsKindAcceptedValues });
                return;
            }

            if (!DaemonServer.TryNormalizeSymbolsPagination(limit, offset, out var normalizedLimit, out var normalizedOffset, out var paginationError))
            {
                JsonOutput.WriteError(
                    paginationError?.Code ?? "INVALID_PARAMS",
                    paginationError?.Message ?? "Invalid symbols pagination parameters.",
                    paginationError?.Details);
                return;
            }

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("symbols", new
                {
                    pattern,
                    kind = normalizedKind,
                    limit = normalizedLimit,
                    offset = normalizedOffset
                });
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }
}
