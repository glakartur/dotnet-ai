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
            Description = "Symbol kind filter: all | type | member | namespace",
            DefaultValueFactory = _ => "all"
        };

        var cmd = new Command("symbols", "Search symbols by name pattern across the solution")
        {
            solutionOption, patternOpt, kindOpt, idleTimeoutOption
        };

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var pattern = parseResult.GetRequiredValue(patternOpt);
            var kind = parseResult.GetRequiredValue(kindOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("symbols", new { pattern, kind });
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }
}
