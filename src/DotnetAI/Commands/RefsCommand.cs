using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class RefsCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var fileOpt   = new Option<FileInfo?>("--file") { Description = "Source file containing the symbol" };
        var lineOpt   = new Option<int?>("--line") { Description = "1-based line number" };
        var colOpt    = new Option<int?>("--col") { Description = "1-based column number" };
        var symbolOpt = new Option<string?>("--symbol") { Description = "Fully-qualified symbol name (alternative to --file/--line/--col)" };

        var cmd = new Command("refs", "Find all references to a symbol")
        {
            solutionOption, fileOpt, lineOpt, colOpt, symbolOpt, idleTimeoutOption
        };

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var file = parseResult.GetValue(fileOpt);
            var line = parseResult.GetValue(lineOpt);
            var col = parseResult.GetValue(colOpt);
            var symbol = parseResult.GetValue(symbolOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            ValidateArgs(file, line, col, symbol);

            var @params = symbol is not null
                ? (object)new { symbol }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value };

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("refs", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }

    private static void ValidateArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        if (symbol is null && (file is null || line is null || col is null))
            throw new ArgumentException(
                "Provide either --symbol OR all of --file --line --col");
    }
}
