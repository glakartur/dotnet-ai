using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Commands.Symbols;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class SymbolsCommand
{
    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null,
        Option<OutputFormat>? formatOption = null)
    {
        var patternOpt = new Option<string>("--pattern")
        {
            Description = "Symbol name pattern (supports * and ? wildcards)",
            Required = true
        };

        var kindOpt = new Option<string>("--kind")
        {
            Description = $"Symbol kind filter: {DaemonServer.SymbolsKindAcceptedValues}",
            DefaultValueFactory = _ => "all"
        };

        var limitOpt = new Option<int>("--limit")
        {
            Description = $"Maximum number of results to return (default: {DaemonServer.SymbolsDefaultLimit}, max: {DaemonServer.SymbolsMaxLimit})",
            DefaultValueFactory = _ => DaemonServer.SymbolsDefaultLimit
        };

        var offsetOpt = new Option<int>("--offset")
        {
            Description = "Number of matching results to skip before collecting output",
            DefaultValueFactory = _ => DaemonServer.SymbolsDefaultOffset
        };

        var cmd = new Command("symbols", "Search symbols by name pattern across the solution")
        {
            solutionOption, patternOpt, kindOpt, limitOpt, offsetOpt, idleTimeoutOption
        };

        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var pattern = parseResult.GetRequiredValue(patternOpt);
            var kind = parseResult.GetRequiredValue(kindOpt);
            var limit = parseResult.GetRequiredValue(limitOpt);
            var offset = parseResult.GetRequiredValue(offsetOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);

            await Entry.ExecuteAsync(solution.FullName, pattern, kind, limit, offset, idleTimeout, format);
        });

        return cmd;
    }
}
