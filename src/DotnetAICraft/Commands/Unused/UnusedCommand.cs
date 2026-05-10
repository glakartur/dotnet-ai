using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Commands.Unused;

namespace DotnetAICraft.Commands;

public static class UnusedCommand
{
    public static Command Build(Option<FileInfo> solutionOption, Option<string?> idleTimeoutOption)
    {
        var kindOpt = new Option<string>("--kind")
        {
            Description = $"Symbol kind filter: {DaemonServer.UnusedKindAcceptedValues}",
            DefaultValueFactory = _ => "all"
        };

        var projectOpt = new Option<string?>("--project")
        {
            Description = "Optional project name filter"
        };

        var publicOnlyOpt = new Option<bool>("--public-only")
        {
            Description = "Analyze only public symbols"
        };

        var includeGeneratedOpt = new Option<bool>("--include-generated")
        {
            Description = "Include generated-code symbols in analysis (default: false)"
        };

        var cmd = new Command("unused", "Find likely unused symbols with confidence and reason")
        {
            solutionOption,
            kindOpt,
            projectOpt,
            publicOnlyOpt,
            includeGeneratedOpt,
            idleTimeoutOption
        };

        cmd.SetAction(async parseResult =>
        {
            var solution = parseResult.GetRequiredValue(solutionOption);
            var kind = parseResult.GetRequiredValue(kindOpt);
            var project = parseResult.GetValue(projectOpt);
            var publicOnly = parseResult.GetValue(publicOnlyOpt);
            var includeGenerated = parseResult.GetValue(includeGeneratedOpt);
            var idleTimeout = parseResult.GetValue(idleTimeoutOption);

            await Entry.ExecuteAsync(solution.FullName, kind, project, publicOnly, includeGenerated, idleTimeout);
        });

        return cmd;
    }
}
