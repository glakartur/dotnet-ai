using DotnetAICraft.Daemon;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DefinitionResolutionTests
{
    [Theory]
    [InlineData(3, 14, "class", null, "Demo", "Sample")]
    [InlineData(5, 16, "property", "Demo.Sample", "Demo", "Value")]
    [InlineData(7, 19, "method", "Demo.Sample", "Demo", "Run")]
    public async Task ResolveDefinitionAsync_FromLocation_ReturnsExpectedSymbolDefinition(
        int line,
        int col,
        string expectedKind,
        string? expectedContainingType,
        string expectedContainingNamespace,
        string expectedNameFragment)
    {
        using var fixture = CreateSolutionFixture();

        var result = await DaemonServer.ResolveDefinitionAsync(
            fixture.Solution,
            symbol: null,
            file: fixture.FilePath,
            line: line,
            col: col);

        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(fixture.FilePath, result.File);
        Assert.Equal(line, result.Line);
        Assert.Equal(expectedContainingType, result.ContainingType);
        Assert.Equal(expectedContainingNamespace, result.ContainingNamespace);
        Assert.Contains(expectedNameFragment, result.FullName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveDefinitionAsync_MetadataOnlySymbol_ReturnsWithoutSourceCoordinates()
    {
        using var fixture = CreateSolutionFixture();

        var result = await DaemonServer.ResolveDefinitionAsync(
            fixture.Solution,
            symbol: null,
            file: fixture.FilePath,
            line: 7,
            col: 12);

        Assert.Equal("class", result.Kind);
        Assert.Null(result.File);
        Assert.Null(result.Line);
        Assert.Null(result.Col);
        Assert.Equal("System", result.ContainingNamespace);
        Assert.Contains("string", result.FullName, StringComparison.OrdinalIgnoreCase);
    }

    private static DefinitionFixture CreateSolutionFixture()
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

        var projectId = ProjectId.CreateNewId(debugName: "DefinitionProject");
        const string projectName = "DefinitionProject";
        const string filePath = "/virtual/src/Sample.cs";

        solution = solution.AddProject(projectId, projectName, projectName, LanguageNames.CSharp);
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(string).Assembly.Location));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Sample.cs",
            SourceText.From("""
namespace Demo;

public class Sample
{
    public int Value { get; set; }

    public string Run() { return string.Empty; }
}
"""),
            filePath: filePath);

        return new DefinitionFixture(workspace, solution, filePath);
    }

    private sealed class DefinitionFixture : IDisposable
    {
        public DefinitionFixture(AdhocWorkspace workspace, Solution solution, string filePath)
        {
            Workspace = workspace;
            Solution = solution;
            FilePath = filePath;
        }

        public AdhocWorkspace Workspace { get; }
        public Solution Solution { get; }
        public string FilePath { get; }

        public void Dispose() => Workspace.Dispose();
    }
}
