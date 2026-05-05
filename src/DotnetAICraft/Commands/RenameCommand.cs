using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class RenameCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
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

            if (symbol is null && (file is null || line is null || col is null))
                throw new ArgumentException(
                    "Provide either --symbol OR all of --file --line --col");

            var @params = symbol is not null
                ? (object)new { symbol, to, dryRun }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value, to, dryRun };

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("rename", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }
}
