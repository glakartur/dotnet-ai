using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class CallersCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var fileOpt   = new Option<FileInfo?>("--file",   "Source file containing the symbol");
        var lineOpt   = new Option<int?>("--line",        "1-based line number");
        var colOpt    = new Option<int?>("--col",         "1-based column number");
        var symbolOpt = new Option<string?>("--symbol",   "Fully-qualified method name");

        var cmd = new Command("callers", "Find all callers of a method (call hierarchy)")
        {
            solutionOption, fileOpt, lineOpt, colOpt, symbolOpt, idleTimeoutOption
        };

        cmd.SetHandler(async (solution, file, line, col, symbol, idleTimeout) =>
        {
            if (symbol is null && (file is null || line is null || col is null))
                throw new ArgumentException(
                    "Provide either --symbol OR all of --file --line --col");

            var @params = symbol is not null
                ? (object)new { symbol }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value };

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("callers", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, fileOpt, lineOpt, colOpt, symbolOpt, idleTimeoutOption);

        return cmd;
    }
}
