using System.CommandLine;
using DotnetAICraft.Commands.Impls;

namespace DotnetAICraft.Commands;

public static class ImplsCommand
{
    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null)
    {
        var symbolOpt = new Option<string>("--symbol")
        {
            Description = "Fully-qualified interface or abstract member name",
            Required = true
        };

        var cmd = new Command("impls",
            "Find all implementations of an interface or abstract member")
        {
            solutionOption, symbolOpt, idleTimeoutOption
        };

        if (debugOption is not null)
            cmd.Add(debugOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var symbol = parseResult.GetRequiredValue(symbolOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            await Entry.ExecuteAsync(solution.FullName, symbol, idleTimeout);
        });

        return cmd;
    }
}
