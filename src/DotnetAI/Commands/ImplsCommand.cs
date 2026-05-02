using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class ImplsCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var symbolOpt = new Option<string>("--symbol",
            "Fully-qualified interface or abstract member name") { IsRequired = true };

        var cmd = new Command("impls",
            "Find all implementations of an interface or abstract member")
        {
            solutionOption, symbolOpt, idleTimeoutOption
        };

        cmd.SetHandler(async (solution, symbol, idleTimeout) =>
        {
            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("impls", new { symbol });
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, symbolOpt, idleTimeoutOption);

        return cmd;
    }
}
