using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Commands.Diagnostics;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands;

public static class DiagnosticsCommand
{
    private const string AcceptedSeverities = "all | error | warning | info | hidden";

    public static Command Build(
        Option<FileInfo> solutionOption,
        Option<string?> idleTimeoutOption,
        Option<bool>? debugOption = null,
        Option<OutputFormat>? formatOption = null)
    {
        var severityOpt = new Option<string>("--severity")
        {
            Description = $"Diagnostic severity filter: {AcceptedSeverities}",
            DefaultValueFactory = _ => "all"
        };

        var projectOpt = new Option<string?>("--project")
        {
            Description = "Optional project name filter"
        };

        var fileOpt = new Option<FileInfo?>("--file")
        {
            Description = "Optional file path filter"
        };

        var cmd = new Command("diagnostics", "List Roslyn diagnostics in JSON format")
        {
            solutionOption, severityOpt, projectOpt, fileOpt, idleTimeoutOption
        };

        if (debugOption is not null)
            cmd.Add(debugOption);
        if (formatOption is not null)
            cmd.Add(formatOption);

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var severity = parseResult.GetRequiredValue(severityOpt);
            var project = parseResult.GetValue(projectOpt);
            var file = parseResult.GetValue(fileOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);
            var format = formatOption is null ? OutputFormat.Text : parseResult.GetValue(formatOption);

            await Entry.ExecuteAsync(solution.FullName, severity, project, file, idleTimeout, AcceptedSeverities, format);
        });

        return cmd;
    }
}
