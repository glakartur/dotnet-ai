using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Commands.Callers;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class CallersCommand
{
    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null,
        Option<OutputFormat>? formatOption = null)
    {
        var fileOpt = new Option<FileInfo?>("--file") { Description = "Source file containing the symbol" };
        var lineOpt = new Option<int?>("--line") { Description = "1-based line number" };
        var colOpt = new Option<int?>("--col") { Description = "1-based column number" };
        var symbolOpt = new Option<string?>("--symbol") { Description = "Fully-qualified method name" };

        var directionOpt = new Option<string>("--direction")
        {
            Description = $"Call graph direction: {DaemonServer.CallGraphDirectionAcceptedValues}",
            DefaultValueFactory = _ => DaemonServer.CallGraphDefaultDirection
        };

        var depthOpt = new Option<int>("--depth")
        {
            Description = $"Call graph traversal depth (min: 1, default: {DaemonServer.CallGraphDefaultDepth})",
            DefaultValueFactory = _ => DaemonServer.CallGraphDefaultDepth
        };

        var cmd = new Command("callers", "Find method callers or callees (call graph)")
        {
            solutionOption,
            fileOpt,
            lineOpt,
            colOpt,
            symbolOpt,
            directionOpt,
            depthOpt,
            idleTimeoutOption
        };

        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var file = parseResult.GetValue(fileOpt);
            var line = parseResult.GetValue(lineOpt);
            var col = parseResult.GetValue(colOpt);
            var symbol = parseResult.GetValue(symbolOpt);
            var direction = parseResult.GetRequiredValue(directionOpt);
            var depth = parseResult.GetRequiredValue(depthOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);

            await Entry.ExecuteAsync(
                solution.FullName,
                file,
                line,
                col,
                symbol,
                direction,
                depth,
                idleTimeout,
                format);
        });

        return cmd;
    }

    private static void ValidateArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        Validation.ValidateCliModeArgs(file, line, col, symbol);
    }
}
