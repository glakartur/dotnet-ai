using System.CommandLine;
using DotnetAICraft.Daemon;
using DotnetAICraft.Output;

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

            if (!DaemonServer.TryParseUnusedKind(kind, out var normalizedKind))
            {
                JsonOutput.WriteError(
                    "INVALID_PARAMS",
                    "Invalid 'kind' parameter.",
                    new { acceptedValues = DaemonServer.UnusedKindAcceptedValues });
                return;
            }

            var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solution.FullName, idleTimeout);
            if (client is null)
                return;

            await using (client)
            {
                var res = await client.SendAsync("unused", new
                {
                    kind = normalizedKind,
                    project,
                    publicOnly,
                    includeGenerated
                });

                if (res.Ok)
                {
                    JsonOutput.Write(res.Result);
                    return;
                }

                var error = res.Error;
                JsonOutput.WriteError(
                    error?.Code ?? "UNKNOWN_ERROR",
                    error?.Message ?? "Unknown daemon error.",
                    error?.Details);
            }
        });

        return cmd;
    }
}
