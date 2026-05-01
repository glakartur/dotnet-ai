using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class ImplsCommand
{
    public static Command Build(Option<FileInfo> solutionOption)
    {
        var symbolOpt = new Option<string>("--symbol",
            "Fully-qualified interface or abstract member name") { IsRequired = true };

        var cmd = new Command("impls",
            "Find all implementations of an interface or abstract member")
        {
            solutionOption, symbolOpt
        };

        cmd.SetHandler(async (solution, symbol) =>
        {
            var client = await DaemonClient.ConnectOrStartAsync(solution.FullName);
            await using (client)
            {
                var res = await client.SendAsync("impls", new { symbol });
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, symbolOpt);

        return cmd;
    }
}
