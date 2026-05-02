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

var solutionOption = new Option<FileInfo>(
    name: "--solution",
    description: "Path to the .sln or .csproj file to analyze")
{
    IsRequired = true
};
solutionOption.AddAlias("-s");

var idleTimeoutOption = new Option<string?>(
    name: "--idle-timeout",
    description: "Daemon idle timeout for this session: 'off' or a positive duration (m|h)");

// ── Root command ──────────────────────────────────────────────────────────────

var root = new RootCommand(
    "dotnet-ai — semantic .NET code analysis for AI agents, powered by Roslyn");

root.AddCommand(ServerCommand.Build(solutionOption, idleTimeoutOption));
root.AddCommand(RefsCommand.Build(solutionOption, idleTimeoutOption));
root.AddCommand(RenameCommand.Build(solutionOption, idleTimeoutOption));
root.AddCommand(ImplsCommand.Build(solutionOption, idleTimeoutOption));
root.AddCommand(CallersCommand.Build(solutionOption, idleTimeoutOption));
root.AddCommand(SymbolsCommand.Build(solutionOption, idleTimeoutOption));

return await root.InvokeAsync(args);
