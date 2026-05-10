using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Commands.Server;

namespace DotnetAICraft.Commands;

public static class ServerCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var cmd = new Command("server", "Manage the analysis daemon");

        cmd.Add(BuildStart(solutionOption, idleTimeoutOption));
        cmd.Add(BuildStop(solutionOption));
        cmd.Add(BuildStatus(solutionOption));
        cmd.Add(BuildReload(solutionOption, idleTimeoutOption));

        return cmd;
    }

    private static Command BuildStart(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var cmd = new Command("start", "Start the daemon (usually called automatically)");
        cmd.Add(solutionOption);
        cmd.Add(idleTimeoutOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            await Entry.StartAsync(solution.FullName, idleTimeout);
        });

        return cmd;
    }

    private static Command BuildStop(Option<FileInfo> solutionOption)
    {
        var cmd = new Command("stop", "Stop the running daemon for this solution");
        cmd.Add(solutionOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            await Entry.StopAsync(solution.FullName);
        });

        return cmd;
    }

    private static Command BuildStatus(Option<FileInfo> solutionOption)
    {
        var cmd = new Command("status", "Show daemon status");
        cmd.Add(solutionOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            await Entry.StatusAsync(solution.FullName);
        });

        return cmd;
    }

    private static Command BuildReload(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var cmd = new Command("reload", "Reload the solution (e.g. after adding/removing projects)");
        cmd.Add(solutionOption);
        cmd.Add(idleTimeoutOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            await Entry.ReloadAsync(solution.FullName, idleTimeout);
        });

        return cmd;
    }
}
