using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Commands.Server;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class ServerCommand
{
    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null,
        Option<OutputFormat>? formatOption = null)
    {
        var cmd = new Command("server", "Manage the analysis daemon");

        cmd.Add(BuildStart(solutionOption, idleTimeoutOption, debugOption, formatOption));
        cmd.Add(BuildDaemon(solutionOption, idleTimeoutOption, debugOption, formatOption));
        cmd.Add(BuildStop(solutionOption, debugOption, formatOption));
        cmd.Add(BuildStatus(solutionOption, debugOption, formatOption));
        cmd.Add(BuildReload(solutionOption, idleTimeoutOption, debugOption, formatOption));

        return cmd;
    }

    private static Command BuildDaemon(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption,
        Option<OutputFormat>? formatOption)
    {
        var cmd = new Command("daemon", "Run the analysis daemon in the foreground (internal use only)")
        {
            Hidden = true,
        };
        cmd.Add(solutionOption);
        cmd.Add(idleTimeoutOption);
        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);
            await Entry.DaemonAsync(solution.FullName, idleTimeout, format);
        });

        return cmd;
    }

    private static Command BuildStart(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption,
        Option<OutputFormat>? formatOption)
    {
        var cmd = new Command("start", "Start the daemon (usually called automatically)");
        cmd.Add(solutionOption);
        cmd.Add(idleTimeoutOption);
        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);
            await Entry.StartAsync(solution.FullName, idleTimeout, format);
        });

        return cmd;
    }

    private static Command BuildStop(Option<FileInfo> solutionOption, Option<bool>? debugOption, Option<OutputFormat>? formatOption)
    {
        var cmd = new Command("stop", "Stop the running daemon for this solution");
        cmd.Add(solutionOption);
        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);
            await Entry.StopAsync(solution.FullName, format);
        });

        return cmd;
    }

    private static Command BuildStatus(Option<FileInfo> solutionOption, Option<bool>? debugOption, Option<OutputFormat>? formatOption)
    {
        var cmd = new Command("status", "Show daemon status");
        cmd.Add(solutionOption);
        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);
            await Entry.StatusAsync(solution.FullName, format);
        });

        return cmd;
    }

    private static Command BuildReload(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption,
        Option<OutputFormat>? formatOption)
    {
        var cmd = new Command("reload", "Reload the solution (e.g. after adding/removing projects)");
        cmd.Add(solutionOption);
        cmd.Add(idleTimeoutOption);
        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);
            await Entry.ReloadAsync(solution.FullName, idleTimeout, format);
        });

        return cmd;
    }
}
