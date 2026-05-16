using System.CommandLine;
using System.CommandLine.Parsing;
using DotnetAICraft.Commands;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Output;
using Microsoft.Build.Locator;

// MSBuild MUST be registered before any Roslyn/MSBuild types are loaded.
// This finds the .NET SDK bundled MSBuild — works on Linux, macOS and Windows.
if (!MSBuildLocator.IsRegistered)
{
    var instances = MSBuildLocator.QueryVisualStudioInstances()
        .OrderByDescending(i => i.Version)
        .ToList();

    var instance = instances.FirstOrDefault()
        ?? throw new InvalidOperationException(
            "Could not find .NET SDK. Make sure 'dotnet' is installed and available in PATH.");

    MSBuildLocator.RegisterInstance(instance);
}

// ── Shared options ────────────────────────────────────────────────────────────

var solutionOption = new Option<FileInfo>("--solution", "-s")
{
    Description = "Path to the .sln or .csproj file to analyze",
    Required = true
};

var idleTimeoutOption = new Option<string?>("--idle-timeout")
{
    Description = "Daemon idle timeout for this session: 'off' or a positive duration (m|h)"
};

var debugOption = new Option<bool>("--debug")
{
    Description = "Enable verbose debug logging to stderr"
};

var formatOption = new Option<OutputFormat>("--format")
{
    Description = "Output format: text (default) | json",
    DefaultValueFactory = _ => OutputFormat.Text,
    CustomParser = ParseOutputFormat
};

// ── Root command ──────────────────────────────────────────────────────────────

var root = new RootCommand(
    "dotnet-aicraft — semantic .NET code analysis for AI agents, powered by Roslyn");

DebugLog.ConfigureFromEnvironment();
DebugLog.ConfigureFromArgs(args);

root.Add(ServerCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(RefsCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(DefinitionCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(RenameCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(ImplsCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(CallersCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(DiagnosticsCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(SymbolsCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));
root.Add(UnusedCommand.Build(solutionOption, idleTimeoutOption, debugOption, formatOption));

return await root.Parse(args).InvokeAsync();

static OutputFormat ParseOutputFormat(ArgumentResult result)
{
    if (result.Tokens.Count == 0)
        return OutputFormat.Text;

    var raw = result.Tokens[0].Value;
    return raw.ToLowerInvariant() switch
    {
        "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        _ => InvalidFormat(result, raw)
    };
}

static OutputFormat InvalidFormat(ArgumentResult result, string raw)
{
    result.AddError($"Invalid --format value '{raw}'. Accepted values: text, json.");
    return OutputFormat.Text;
}
