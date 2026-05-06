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

public class SymbolsPaginationTests
{
    [Fact]
    public void TryNormalizeSymbolsPagination_DefaultsAndCap_AreApplied()
    {
        var defaultsOk = DaemonServer.TryNormalizeSymbolsPagination(
            limit: null,
            offset: null,
            out var defaultLimit,
            out var defaultOffset,
            out var defaultError);

        Assert.True(defaultsOk);
        Assert.Null(defaultError);
        Assert.Equal(DaemonServer.SymbolsDefaultLimit, defaultLimit);
        Assert.Equal(DaemonServer.SymbolsDefaultOffset, defaultOffset);

        var cappedOk = DaemonServer.TryNormalizeSymbolsPagination(
            limit: DaemonServer.SymbolsMaxLimit + 500,
            offset: 10,
            out var cappedLimit,
            out var cappedOffset,
            out var cappedError);

        Assert.True(cappedOk);
        Assert.Null(cappedError);
        Assert.Equal(DaemonServer.SymbolsMaxLimit, cappedLimit);
        Assert.Equal(10, cappedOffset);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void TryNormalizeSymbolsPagination_InvalidValues_ReturnValidationError(int limit, int offset)
    {
        var ok = DaemonServer.TryNormalizeSymbolsPagination(limit, offset, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal("INVALID_PARAMS", error.Code);
    }

    [Fact]
    public async Task CollectSymbolsAsync_LimitOne_ReturnsExactlyOneRecordAndHasMore_WhenMatchesExist()
    {
        using var fixture = CreateSymbolsFixture();

        var result = await DaemonServer.CollectSymbolsAsync(
            fixture.Solution,
            pattern: "PaginationSymbolType*",
            filter: SymbolFilter.Type,
            limit: 1,
            offset: 0);

        Assert.Single(result.Items);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task CollectSymbolsAsync_LargeOffset_ReturnsEmptyListWithoutError()
    {
        using var fixture = CreateSymbolsFixture();

        var result = await DaemonServer.CollectSymbolsAsync(
            fixture.Solution,
            pattern: "PaginationSymbolType*",
            filter: SymbolFilter.Type,
            limit: 10,
            offset: 10_000);

        Assert.Empty(result.Items);
        Assert.False(result.HasMore);
    }

    private static SymbolsFixture CreateSymbolsFixture()
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

        var projectId = ProjectId.CreateNewId(debugName: "SymbolsProject");
        const string projectName = "SymbolsProject";

        solution = solution.AddProject(projectId, projectName, projectName, LanguageNames.CSharp);
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Symbols.cs",
            SourceText.From("""
namespace Demo;

public class PaginationSymbolTypeAlpha { }
public class PaginationSymbolTypeBeta { }
"""),
            filePath: "/virtual/src/Symbols.cs");

        return new SymbolsFixture(workspace, solution);
    }

    private sealed class SymbolsFixture : IDisposable
    {
        public SymbolsFixture(AdhocWorkspace workspace, Solution solution)
        {
            Workspace = workspace;
            Solution = solution;
        }

        public AdhocWorkspace Workspace { get; }
        public Solution Solution { get; }

        public void Dispose() => Workspace.Dispose();
    }
}

public class SymbolsKindMappingTests
{
    private static readonly string[] TypeKinds = ["class", "interface", "struct", "enum", "delegate"];
    private static readonly string[] MemberKinds = ["method", "constructor", "property", "field", "event"];

    [Theory]
    [InlineData("all", "all", SymbolFilter.All)]
    [InlineData("type", "type", SymbolFilter.Type)]
    [InlineData("member", "member", SymbolFilter.Member)]
    [InlineData("namespace", "namespace", SymbolFilter.Namespace)]
    [InlineData("class", "class", SymbolFilter.Type)]
    [InlineData("interface", "interface", SymbolFilter.Type)]
    [InlineData("struct", "struct", SymbolFilter.Type)]
    [InlineData("enum", "enum", SymbolFilter.Type)]
    [InlineData("delegate", "delegate", SymbolFilter.Type)]
    [InlineData("method", "method", SymbolFilter.Member)]
    [InlineData("constructor", "constructor", SymbolFilter.Type)]
    [InlineData("property", "property", SymbolFilter.Member)]
    [InlineData("field", "field", SymbolFilter.Member)]
    [InlineData("event", "event", SymbolFilter.Member)]
    [InlineData(" Method ", "method", SymbolFilter.Member)]
    public void TryParseSymbolsKind_MapsAllSupportedValues(
        string input,
        string expectedNormalized,
        SymbolFilter expectedFilter)
    {
        var ok = DaemonServer.TryParseSymbolsKind(input, out var filter, out var predicate, out var normalized);

        Assert.True(ok);
        Assert.Equal(expectedNormalized, normalized);
        Assert.Equal(expectedFilter, filter);
        Assert.NotNull(predicate);
    }

    [Theory]
    [InlineData("class", "KindTargetClass*")]
    [InlineData("interface", "KindTargetInterface*")]
    [InlineData("struct", "KindTargetStruct*")]
    [InlineData("enum", "KindTargetEnum*")]
    [InlineData("delegate", "KindTargetDelegate*")]
    [InlineData("method", "KindTargetMethod*")]
    [InlineData("constructor", "KindTarget*")]
    [InlineData("property", "KindTargetProperty*")]
    [InlineData("field", "KindTargetField*")]
    [InlineData("event", "KindTargetEvent*")]
    [InlineData("namespace", "KindTargetNamespace*")]
    public async Task CollectSymbolsAsync_GranularKinds_ReturnOnlyRequestedSubset(string kind, string pattern)
    {
        using var fixture = CreateSymbolsKindsFixture();

        var result = await DaemonServer.CollectSymbolsAsync(
            fixture.Solution,
            pattern: pattern,
            kind: kind,
            limit: 500);

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item => Assert.Equal(kind, item.Kind));
    }

    [Fact]
    public async Task CollectSymbolsAsync_TypeAndMember_KeepBackwardCompatibility()
    {
        using var fixture = CreateSymbolsKindsFixture();

        var typeResult = await DaemonServer.CollectSymbolsAsync(
            fixture.Solution,
            pattern: "KindTarget*",
            kind: "type",
            limit: 500);

        Assert.NotEmpty(typeResult.Items);
        Assert.All(typeResult.Items, item => Assert.Contains(item.Kind, TypeKinds));
        AssertContainsKind(typeResult.Items, "class");
        AssertContainsKind(typeResult.Items, "interface");
        AssertContainsKind(typeResult.Items, "struct");
        AssertContainsKind(typeResult.Items, "enum");
        AssertContainsKind(typeResult.Items, "delegate");

        var memberResult = await DaemonServer.CollectSymbolsAsync(
            fixture.Solution,
            pattern: "KindTarget*",
            kind: "member",
            limit: 500);

        Assert.NotEmpty(memberResult.Items);
        Assert.All(memberResult.Items, item => Assert.Contains(item.Kind, MemberKinds));
        AssertContainsKind(memberResult.Items, "method");
        AssertContainsKind(memberResult.Items, "property");
        AssertContainsKind(memberResult.Items, "field");
        AssertContainsKind(memberResult.Items, "event");
    }

    [Fact]
    public async Task CollectSymbolsAsync_InvalidKind_ReturnsValidationError()
    {
        using var fixture = CreateSymbolsKindsFixture();

        var ex = await Assert.ThrowsAsync<DaemonValidationException>(() =>
            DaemonServer.CollectSymbolsAsync(
                fixture.Solution,
                pattern: "KindTarget*",
                kind: "invalid-kind"));

        Assert.Equal("INVALID_PARAMS", ex.Error.Code);
        Assert.Equal("Invalid 'kind' parameter.", ex.Error.Message);
    }

    private static SymbolsKindsFixture CreateSymbolsKindsFixture()
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

        var projectId = ProjectId.CreateNewId(debugName: "SymbolsKindsProject");
        const string projectName = "SymbolsKindsProject";

        solution = solution.AddProject(projectId, projectName, projectName, LanguageNames.CSharp);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "SymbolsKinds.cs",
            SourceText.From("""
namespace KindTargetNamespace;

public delegate void KindTargetDelegate();

public interface KindTargetInterface
{
    void KindTargetMethod();
    KindTargetEnum KindTargetProperty { get; }
    event KindTargetDelegate KindTargetEvent;
}

public struct KindTargetStruct
{
    public KindTargetEnum KindTargetField;
    public KindTargetStruct(KindTargetEnum value) => KindTargetField = value;
    public KindTargetEnum KindTargetProperty { get; set; }
    public event KindTargetDelegate KindTargetEvent;
    public void KindTargetMethod() { }
}

public enum KindTargetEnum
{
    KindTargetEnumValue
}

public class KindTargetClass
{
    public KindTargetEnum KindTargetField;
    public KindTargetClass() { }
    public KindTargetEnum KindTargetProperty { get; set; }
    public event KindTargetDelegate KindTargetEvent;
    public void KindTargetMethod() { }
}
"""),
            filePath: "/virtual/src/SymbolsKinds.cs");

        return new SymbolsKindsFixture(workspace, solution);
    }

    private static void AssertContainsKind(IReadOnlyList<DotnetAICraft.Models.SymbolResult> items, string kind)
        => Assert.Contains(items, item => string.Equals(item.Kind, kind, StringComparison.Ordinal));

    private sealed class SymbolsKindsFixture : IDisposable
    {
        public SymbolsKindsFixture(AdhocWorkspace workspace, Solution solution)
        {
            Workspace = workspace;
            Solution = solution;
        }

        public AdhocWorkspace Workspace { get; }
        public Solution Solution { get; }

        public void Dispose() => Workspace.Dispose();
    }
}


