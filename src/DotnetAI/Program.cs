using System.CommandLine;
using DotnetAi.Commands;
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

// ── Root command ──────────────────────────────────────────────────────────────

var root = new RootCommand(
    "dotnet-ai — semantic .NET code analysis for AI agents, powered by Roslyn");

root.Add(ServerCommand.Build(solutionOption, idleTimeoutOption));
root.Add(RefsCommand.Build(solutionOption, idleTimeoutOption));
root.Add(RenameCommand.Build(solutionOption, idleTimeoutOption));
root.Add(ImplsCommand.Build(solutionOption, idleTimeoutOption));
root.Add(CallersCommand.Build(solutionOption, idleTimeoutOption));
root.Add(SymbolsCommand.Build(solutionOption, idleTimeoutOption));

return await root.Parse(args).InvokeAsync();
