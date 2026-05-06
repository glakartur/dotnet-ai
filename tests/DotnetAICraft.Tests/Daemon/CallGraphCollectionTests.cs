using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class CallGraphDirectionAndDepthValidationTests
{
    [Theory]
    [InlineData(null, true, "incoming")]
    [InlineData("incoming", true, "incoming")]
    [InlineData("outgoing", true, "outgoing")]
    [InlineData("both", true, "both")]
    [InlineData(" BOTH ", true, "both")]
    [InlineData("invalid", false, "invalid")]
    public void TryParseCallGraphDirection_MapsExpectedValues(
        string? input,
        bool expectedSuccess,
        string expectedNormalized)
    {
        var ok = DaemonServer.TryParseCallGraphDirection(input, out var normalized);

        Assert.Equal(expectedSuccess, ok);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData(null, true, 1)]
    [InlineData(1, true, 1)]
    [InlineData(3, true, 3)]
    [InlineData(0, false, 0)]
    [InlineData(-1, false, -1)]
    public void TryNormalizeCallGraphDepth_ValidatesMinimum(
        int? input,
        bool expectedSuccess,
        int expectedDepth)
    {
        var ok = DaemonServer.TryNormalizeCallGraphDepth(input, out var normalized, out var error);

        Assert.Equal(expectedSuccess, ok);
        Assert.Equal(expectedDepth, normalized);

        if (expectedSuccess)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Equal("INVALID_PARAMS", error.Code);
        }
    }
}

public class CallGraphCollectionTests
{
    [Fact]
    public async Task CollectCallGraphAsync_OutgoingDepth2_ReturnsChainAndBranch()
    {
        using var fixture = CreateFixture();
        var methodA = await ResolveMethodAsync(fixture.Solution, "A");

        var graph = await DaemonServer.CollectCallGraphAsync(
            fixture.Solution,
            methodA,
            direction: "outgoing",
            depth: 2);

        Assert.Equal("outgoing", graph.Direction);
        Assert.Equal(2, graph.Depth);

        var aId = NodeIdForMethod(graph, "A");
        var bId = NodeIdForMethod(graph, "B");
        var cId = NodeIdForMethod(graph, "C");

        AssertContainsEdge(graph.Edges, aId, bId, "outgoing");
        AssertContainsEdge(graph.Edges, aId, cId, "outgoing");
        AssertContainsEdge(graph.Edges, bId, cId, "outgoing");
    }

    [Fact]
    public async Task CollectCallGraphAsync_OutgoingCycle_DoesNotLoopIndefinitely()
    {
        using var fixture = CreateFixture();
        var methodD = await ResolveMethodAsync(fixture.Solution, "D");

        var graph = await DaemonServer.CollectCallGraphAsync(
            fixture.Solution,
            methodD,
            direction: "outgoing",
            depth: 5);

        var dId = NodeIdForMethod(graph, "D");
        var eId = NodeIdForMethod(graph, "E");

        Assert.Equal(2, graph.Edges.Count);
        AssertContainsEdge(graph.Edges, dId, eId, "outgoing");
        AssertContainsEdge(graph.Edges, eId, dId, "outgoing");
    }

    [Fact]
    public async Task CollectCallGraphAsync_BothDirection_CombinesIncomingAndOutgoing()
    {
        using var fixture = CreateFixture();
        var methodB = await ResolveMethodAsync(fixture.Solution, "B");

        var graph = await DaemonServer.CollectCallGraphAsync(
            fixture.Solution,
            methodB,
            direction: "both",
            depth: 1);

        var aId = NodeIdForMethod(graph, "A");
        var bId = NodeIdForMethod(graph, "B");
        var cId = NodeIdForMethod(graph, "C");

        AssertContainsEdge(graph.Edges, aId, bId, "incoming");
        AssertContainsEdge(graph.Edges, bId, cId, "outgoing");
    }

    [Fact]
    public async Task CollectCallGraphAsync_InvalidDirection_ThrowsValidationException()
    {
        using var fixture = CreateFixture();
        var methodC = await ResolveMethodAsync(fixture.Solution, "C");

        var ex = await Assert.ThrowsAsync<DaemonValidationException>(() =>
            DaemonServer.CollectCallGraphAsync(
                fixture.Solution,
                methodC,
                direction: "sideways",
                depth: 1));

        Assert.Equal("INVALID_PARAMS", ex.Error.Code);
        Assert.Equal("Invalid 'direction' parameter.", ex.Error.Message);
    }

    [Fact]
    public async Task CollectCallGraphAsync_DepthZero_ThrowsValidationException()
    {
        using var fixture = CreateFixture();
        var methodC = await ResolveMethodAsync(fixture.Solution, "C");

        var ex = await Assert.ThrowsAsync<DaemonValidationException>(() =>
            DaemonServer.CollectCallGraphAsync(
                fixture.Solution,
                methodC,
                direction: "incoming",
                depth: 0));

        Assert.Equal("INVALID_PARAMS", ex.Error.Code);
        Assert.Equal("Parameter 'depth' must be greater than or equal to 1.", ex.Error.Message);
    }

    [Fact]
    public async Task CollectIncomingCallersAsync_Regression_ReturnsExpectedCallersForC()
    {
        using var fixture = CreateFixture();
        var methodC = await ResolveMethodAsync(fixture.Solution, "C");

        var callers = await DaemonServer.CollectIncomingCallersAsync(fixture.Solution, methodC);

        Assert.Equal(2, callers.Count);
        Assert.Contains(callers, c => c.CallerSymbol.Contains("CallGraphSample.A()", StringComparison.Ordinal));
        Assert.Contains(callers, c => c.CallerSymbol.Contains("CallGraphSample.B()", StringComparison.Ordinal));
    }

    private static async Task<IMethodSymbol> ResolveMethodAsync(Solution solution, string methodName)
    {
        var project = Assert.Single(solution.Projects);
        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        var type = compilation!.GetTypeByMetadataName("Demo.CallGraphSample");
        Assert.NotNull(type);

        return Assert.Single(type!.GetMembers(methodName).OfType<IMethodSymbol>());
    }

    private static string NodeIdForMethod(CallGraphResult graph, string methodName)
    {
        var node = Assert.Single(
            graph.Nodes,
            n => n.FullName.Contains($"CallGraphSample.{methodName}(", StringComparison.Ordinal));

        return node.Id;
    }

    private static void AssertContainsEdge(
        IReadOnlyList<CallGraphEdge> edges,
        string from,
        string to,
        string relation)
        => Assert.Contains(edges, edge =>
            edge.From == from &&
            edge.To == to &&
            edge.Relation == relation);

    private static CallGraphFixture CreateFixture()
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

        var projectId = ProjectId.CreateNewId(debugName: "CallGraphProject");

        solution = solution.AddProject(projectId, "CallGraphProject", "CallGraphProject", LanguageNames.CSharp);
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "CallGraphSample.cs",
            SourceText.From("""
namespace Demo;

public class CallGraphSample
{
    public void A()
    {
        B();
        C();
    }

    public void B()
    {
        C();
    }

    public void C()
    {
    }

    public void D()
    {
        E();
    }

    public void E()
    {
        D();
    }
}
"""),
            filePath: "/virtual/src/CallGraphSample.cs");

        return new CallGraphFixture(workspace, solution);
    }

    private sealed class CallGraphFixture : IDisposable
    {
        public CallGraphFixture(AdhocWorkspace workspace, Solution solution)
        {
            Workspace = workspace;
            Solution = solution;
        }

        public AdhocWorkspace Workspace { get; }
        public Solution Solution { get; }

        public void Dispose() => Workspace.Dispose();
    }
}
