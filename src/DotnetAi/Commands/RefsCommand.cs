using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class RefsCommand
{
    public static Command Build(Option<FileInfo> solutionOption)
    {
        var fileOpt   = new Option<FileInfo?>("--file",   "Source file containing the symbol");
        var lineOpt   = new Option<int?>("--line",        "1-based line number");
        var colOpt    = new Option<int?>("--col",         "1-based column number");
        var symbolOpt = new Option<string?>("--symbol",   "Fully-qualified symbol name (alternative to --file/--line/--col)");

        var cmd = new Command("refs", "Find all references to a symbol")
        {
            solutionOption, fileOpt, lineOpt, colOpt, symbolOpt
        };

        cmd.SetHandler(async (solution, file, line, col, symbol) =>
        {
            ValidateArgs(file, line, col, symbol);

            var @params = symbol is not null
                ? (object)new { symbol }
                : new { file = file!.FullName, line = line!.Value, col = col!.Value };

            var client = await DaemonClient.ConnectOrStartAsync(solution.FullName);
            await using (client)
            {
                var res = await client.SendAsync("refs", @params);
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, fileOpt, lineOpt, colOpt, symbolOpt);

        return cmd;
    }

    private static void ValidateArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        if (symbol is null && (file is null || line is null || col is null))
            throw new ArgumentException(
                "Provide either --symbol OR all of --file --line --col");
    }
}
