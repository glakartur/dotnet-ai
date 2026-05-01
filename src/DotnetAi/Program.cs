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

// ── Root command ──────────────────────────────────────────────────────────────

var root = new RootCommand(
    "dotnet-ai — semantic .NET code analysis for AI agents, powered by Roslyn");

root.AddCommand(ServerCommand.Build(solutionOption));
root.AddCommand(RefsCommand.Build(solutionOption));
root.AddCommand(RenameCommand.Build(solutionOption));
root.AddCommand(ImplsCommand.Build(solutionOption));
root.AddCommand(CallersCommand.Build(solutionOption));
root.AddCommand(SymbolsCommand.Build(solutionOption));

return await root.InvokeAsync(args);
