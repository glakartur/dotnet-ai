using System.CommandLine;
using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

public static class ServerCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var cmd = new Command("server", "Manage the analysis daemon");

        cmd.AddCommand(BuildStart(solutionOption, idleTimeoutOption));
        cmd.AddCommand(BuildStop(solutionOption));
        cmd.AddCommand(BuildStatus(solutionOption));
        cmd.AddCommand(BuildReload(solutionOption, idleTimeoutOption));

        return cmd;
    }

    private static Command BuildStart(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var cmd = new Command("start", "Start the daemon (usually called automatically)");
        cmd.AddOption(solutionOption);
        cmd.AddOption(idleTimeoutOption);

        cmd.SetHandler(async (solution, idleTimeout) =>
        {
            if (!DaemonIdleTimeoutParser.TryParseOptional(idleTimeout, out var timeout, out var error))
            {
                JsonOutput.WriteError(error!.Code, error.Message, error.Details);
                return;
            }

            await using var server = new DaemonServer(solution.FullName, timeout);
            await server.RunAsync();
        }, solutionOption, idleTimeoutOption);

        return cmd;
    }

    private static Command BuildStop(Option<FileInfo> solutionOption)
    {
        var cmd = new Command("stop", "Stop the running daemon for this solution");
        cmd.AddOption(solutionOption);

        cmd.SetHandler(async (solution) =>
        {
            var client = await DaemonClient.TryConnectAsync(solution.FullName);
            if (client is null)
            {
                JsonOutput.WriteError("DAEMON_NOT_RUNNING", "No daemon running for this solution.");
                return;
            }
            await using (client)
            {
                var res = await client.SendAsync("shutdown");
                JsonOutput.Write(res.Result);
            }
        }, solutionOption);

        return cmd;
    }

    private static Command BuildStatus(Option<FileInfo> solutionOption)
    {
        var cmd = new Command("status", "Show daemon status");
        cmd.AddOption(solutionOption);

        cmd.SetHandler(async (solution) =>
        {
            var client = await DaemonClient.TryConnectAsync(solution.FullName);
            if (client is null)
            {
                JsonOutput.Write(new { running = false, solutionPath = solution.FullName });
                return;
            }
            await using (client)
            {
                var res = await client.SendAsync("status");
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption);

        return cmd;
    }

    private static Command BuildReload(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var cmd = new Command("reload", "Reload the solution (e.g. after adding/removing projects)");
        cmd.AddOption(solutionOption);
        cmd.AddOption(idleTimeoutOption);

        cmd.SetHandler(async (solution, idleTimeout) =>
        {
            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("reload");
                JsonOutput.Write(res.Ok ? res.Result : (object)res.Error!);
            }
        }, solutionOption, idleTimeoutOption);

        return cmd;
    }
}
