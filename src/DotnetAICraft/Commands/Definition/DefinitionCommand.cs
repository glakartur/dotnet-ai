using System.CommandLine;
using DotnetAICraft.Commands.Definition;

namespace DotnetAICraft.Commands;

public static class DefinitionCommand
{
    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null)
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

        if (debugOption is not null)
            cmd.Add(debugOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var file = parseResult.GetValue(fileOpt);
            var line = parseResult.GetValue(lineOpt);
            var col = parseResult.GetValue(colOpt);
            var symbol = parseResult.GetValue(symbolOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            await Entry.ExecuteAsync(solution.FullName, file, line, col, symbol, idleTimeout);
        });

        return cmd;
    }

}
