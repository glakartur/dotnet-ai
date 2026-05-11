using System.CommandLine;
using DotnetAICraft.Commands.Rename;

namespace DotnetAICraft.Commands;

public static class RenameCommand
{
    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null)
    {
        var fileOpt   = new Option<FileInfo?>("--file") { Description = "Source file containing the symbol" };
        var lineOpt   = new Option<int?>("--line") { Description = "1-based line number" };
        var colOpt    = new Option<int?>("--col") { Description = "1-based column number" };
        var symbolOpt = new Option<string?>("--symbol") { Description = "Fully-qualified symbol name" };
        var toOpt     = new Option<string>("--to")
        {
            Description = "New name for the symbol",
            Required = true
        };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Preview changes without applying them" };

        var cmd = new Command("rename", "Rename a symbol across the entire solution")
        {
            solutionOption, fileOpt, lineOpt, colOpt, symbolOpt, toOpt, dryRunOpt, idleTimeoutOption
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
            var to = parseResult.GetRequiredValue(toOpt);
            var dryRun = parseResult.GetValue(dryRunOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            await Entry.ExecuteAsync(solution.FullName, file, line, col, symbol, to, dryRun, idleTimeout);
        });

        return cmd;
    }
}
