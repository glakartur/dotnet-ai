using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class DefinitionCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var fileOpt = new Option<FileInfo?>("--file")
        {
            Description = "Source file containing symbol usage/declaration"
        };

        var lineOpt = new Option<int?>("--line")
        {
            Description = "1-based line number"
        };

        var colOpt = new Option<int?>("--col")
        {
            Description = "1-based column number"
        };

        var symbolOpt = new Option<string?>("--symbol")
        {
            Description = "Fully-qualified symbol name (alternative to --file/--line/--col)"
        };

        var cmd = new Command("definition", "Find symbol declaration by location or fully-qualified name")
        {
            solutionOption,
            fileOpt,
            lineOpt,
            colOpt,
            symbolOpt,
            idleTimeoutOption
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

            var @params = !string.IsNullOrWhiteSpace(symbol)
                ? (object)new { symbol = symbol.Trim() }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value };

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("definition", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }

    private static void ValidateArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        var hasSymbol = !string.IsNullOrWhiteSpace(symbol);
        var hasAnyLocation = file is not null || line is not null || col is not null;
        var hasCompleteLocation = file is not null && line is not null && col is not null;

        if (hasSymbol == hasAnyLocation)
            throw new ArgumentException(
                "Provide exactly one input mode: either --symbol OR --file --line --col");

        if (hasAnyLocation && !hasCompleteLocation)
            throw new ArgumentException(
                "Location mode requires all of --file --line --col");
    }
}
