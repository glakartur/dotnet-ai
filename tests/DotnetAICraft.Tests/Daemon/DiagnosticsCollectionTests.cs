using DotnetAICraft.Daemon;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DiagnosticsCollectionTests
{
    [Theory]
    [InlineData("all", true, null, "all")]
    [InlineData("error", true, DiagnosticSeverity.Error, "error")]
    [InlineData("warning", true, DiagnosticSeverity.Warning, "warning")]
    [InlineData("info", true, DiagnosticSeverity.Info, "info")]
    [InlineData("hidden", true, DiagnosticSeverity.Hidden, "hidden")]
    [InlineData(" Warning ", true, DiagnosticSeverity.Warning, "warning")]
    [InlineData("invalid", false, null, "invalid")]
    public void TryParseDiagnosticsSeverity_MapsExpectedValues(
        string input,
        bool expectedSuccess,
        DiagnosticSeverity? expectedSeverity,
        string expectedNormalized)
    {
        var ok = DaemonServer.TryParseDiagnosticsSeverity(input, out var severity, out var normalized);

        Assert.Equal(expectedSuccess, ok);
        Assert.Equal(expectedSeverity, severity);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Fact]
    public async Task CollectDiagnosticsAsync_ProjectWithCompilationError_ReturnsAtLeastOneRecord()
    {
        using var fixture = CreateSolutionFixture();

        var diagnostics = await DaemonServer.CollectDiagnosticsAsync(fixture.Solution);

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d =>
            d.Project == fixture.BrokenProjectName &&
            d.File == fixture.BrokenFilePath);
    }

    [Fact]
    public async Task CollectDiagnosticsAsync_ProjectAndFileFilters_ReturnOnlyMatchingRecords()
    {
        using var fixture = CreateSolutionFixture();

        var filteredByProject = await DaemonServer.CollectDiagnosticsAsync(
            fixture.Solution,
            projectFilter: fixture.BrokenProjectName);

        Assert.NotEmpty(filteredByProject);
        Assert.All(filteredByProject, d => Assert.Equal(fixture.BrokenProjectName, d.Project));

        var filteredByFile = await DaemonServer.CollectDiagnosticsAsync(
            fixture.Solution,
            fileFilter: fixture.BrokenFilePath);

        Assert.NotEmpty(filteredByFile);
        Assert.All(filteredByFile, d => Assert.Equal(fixture.BrokenFilePath, d.File));
    }

    private static DiagnosticsFixture CreateSolutionFixture()
    {
        var assemblies = MefHostServices.DefaultAssemblies
            .Concat(new[]
            {
                typeof(CSharpCompilation).Assembly,
                typeof(CSharpFormattingOptions).Assembly
            })
            .Distinct();

        var host = MefHostServices.Create(assemblies);
        var workspace = new AdhocWorkspace(host);
        var solution = workspace.CurrentSolution;

        var brokenProjectId = ProjectId.CreateNewId(debugName: "BrokenProject");
        var healthyProjectId = ProjectId.CreateNewId(debugName: "HealthyProject");

        const string brokenProjectName = "BrokenProject";
        const string healthyProjectName = "HealthyProject";
        const string brokenFilePath = "/virtual/src/Broken.cs";
        const string healthyFilePath = "/virtual/src/Healthy.cs";

        solution = solution.AddProject(brokenProjectId, brokenProjectName, brokenProjectName, LanguageNames.CSharp);
        solution = solution.AddMetadataReference(
            brokenProjectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(brokenProjectId),
            "Broken.cs",
            SourceText.From("public class Broken { public int Get() { return \"oops\"; } }"),
            filePath: brokenFilePath);

        solution = solution.AddProject(healthyProjectId, healthyProjectName, healthyProjectName, LanguageNames.CSharp);
        solution = solution.AddMetadataReference(
            healthyProjectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(healthyProjectId),
            "Healthy.cs",
            SourceText.From("public class Healthy { public int Get() { return 42; } }"),
            filePath: healthyFilePath);

        return new DiagnosticsFixture(workspace, solution, brokenProjectName, brokenFilePath);
    }

    private sealed class DiagnosticsFixture : IDisposable
    {
        public DiagnosticsFixture(
            AdhocWorkspace workspace,
            Solution solution,
            string brokenProjectName,
            string brokenFilePath)
        {
            Workspace = workspace;
            Solution = solution;
            BrokenProjectName = brokenProjectName;
            BrokenFilePath = brokenFilePath;
        }

        public AdhocWorkspace Workspace { get; }
        public Solution Solution { get; }
        public string BrokenProjectName { get; }
        public string BrokenFilePath { get; }

        public void Dispose() => Workspace.Dispose();
    }
}
