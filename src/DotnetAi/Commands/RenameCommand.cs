using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class RenameCommand
{
    public static Command Build(Option<FileInfo> solutionOption)
    {
        var fileOpt   = new Option<FileInfo?>("--file",   "Source file containing the symbol");
        var lineOpt   = new Option<int?>("--line",        "1-based line number");
        var colOpt    = new Option<int?>("--col",         "1-based column number");
        var symbolOpt = new Option<string?>("--symbol",   "Fully-qualified symbol name");
        var toOpt     = new Option<string>("--to",        "New name for the symbol") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run",     "Preview changes without applying them");

        var cmd = new Command("rename", "Rename a symbol across the entire solution")
        {
            solutionOption, fileOpt, lineOpt, colOpt, symbolOpt, toOpt, dryRunOpt
        };

        cmd.SetHandler(async (solution, file, line, col, symbol, to, dryRun) =>
        {
            if (symbol is null && (file is null || line is null || col is null))
                throw new ArgumentException(
                    "Provide either --symbol OR all of --file --line --col");

            var @params = symbol is not null
                ? (object)new { symbol, to, dryRun }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value, to, dryRun };

            var client = await DaemonClient.ConnectOrStartAsync(solution.FullName);
            await using (client)
            {
                var res = await client.SendAsync("rename", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, fileOpt, lineOpt, colOpt, symbolOpt, toOpt, dryRunOpt);

        return cmd;
    }
}
