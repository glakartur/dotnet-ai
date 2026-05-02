using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class SymbolsCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var patternOpt = new Option<string>("--pattern",
            "Symbol name pattern (supports * and ? wildcards)") { IsRequired = true };

        var kindOpt = new Option<string>("--kind", () => "all",
            "Symbol kind filter: all | type | member | namespace");

        var cmd = new Command("symbols", "Search symbols by name pattern across the solution")
        {
            solutionOption, patternOpt, kindOpt, idleTimeoutOption
        };

        cmd.SetHandler(async (solution, pattern, kind, idleTimeout) =>
        {
            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("symbols", new { pattern, kind });
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, patternOpt, kindOpt, idleTimeoutOption);

        return cmd;
    }
}
