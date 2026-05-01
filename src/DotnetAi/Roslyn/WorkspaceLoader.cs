using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotnetAi.Roslyn;

public static class WorkspaceLoader
{
    public static async Task<(MSBuildWorkspace Workspace, Solution Solution)> LoadAsync(
        string path,
        CancellationToken ct = default)
    {
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            // Ensures design-time build — avoids running generators that need network etc.
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true"
        });

        // Surface workspace diagnostics to stderr (not stdout — stdout is reserved for JSON)
        workspace.WorkspaceFailed += (_, e) =>
        {
            var kind = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERR" : "WRN";
            Console.Error.WriteLine($"[workspace:{kind}] {e.Diagnostic.Message}");
        };

        Solution solution;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".sln")
        {
            solution = await workspace.OpenSolutionAsync(path, cancellationToken: ct);
        }
        else if (ext == ".csproj" || ext == ".vbproj" || ext == ".fsproj")
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: ct);
            solution = project.Solution;
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported file type '{ext}'. Use .sln, .csproj, .vbproj or .fsproj.");
        }

        return (workspace, solution);
    }
}
