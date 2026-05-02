using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class ImplsCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
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

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var symbol = parseResult.GetRequiredValue(symbolOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("impls", new { symbol });
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        });

        return cmd;
    }
}
