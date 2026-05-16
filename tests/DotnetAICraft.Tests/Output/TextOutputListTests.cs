using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class TextOutputListTests
{
    [Fact]
    public void Refs_Happy_RendersHeaderAndRows()
    {
        var items = new[]
        {
            new ReferenceResult("/a/File1.cs", 10, 4, "var x = 1;"),
            new ReferenceResult("/a/File2.cs", 20, 8, "y = x;"),
            new ReferenceResult("/a/File3.cs", 30, 1, "x;")
        };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRefs(items, "Demo.Sample", "MySolution.sln");
        var lines = cap.GetOutput().Split(Environment.NewLine);
        Assert.Equal("3 references to Demo.Sample in MySolution.sln", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("/a/File1.cs:10:4: var x = 1;", lines[2]);
        Assert.Equal("/a/File2.cs:20:8: y = x;", lines[3]);
        Assert.Equal("/a/File3.cs:30:1: x;", lines[4]);
    }

    [Fact]
    public void Refs_Singular_PluralForm()
    {
        var items = new[] { new ReferenceResult("/a/F.cs", 1, 1, "x") };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRefs(items, "T", "S.sln");
        Assert.StartsWith("1 reference to T in S.sln", cap.GetOutput());
    }

    [Fact]
    public void Refs_Empty_HeaderOnly()
    {
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRefs(Array.Empty<ReferenceResult>(), "T", "S.sln");
        var text = cap.GetOutput();
        Assert.Equal($"0 references to T in S.sln{Environment.NewLine}", text);
    }

    [Fact]
    public void Refs_PathWithDriveColon_PreservedAsIs()
    {
        var items = new[] { new ReferenceResult("C:/path/Foo.cs", 42, 17, "body") };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRefs(items, "T", "S.sln");
        Assert.Contains("C:/path/Foo.cs:42:17: body", cap.GetOutput());
    }

    [Fact]
    public void Refs_BodyNewlines_RenderedOnOneLine()
    {
        var items = new[] { new ReferenceResult("/a/F.cs", 1, 1, "line1\nline2") };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRefs(items, "T", "S.sln");
        var text = cap.GetOutput();
        // The body line should not contain a newline between "line1" and "line2"
        Assert.Contains("/a/F.cs:1:1: line1 line2", text);
    }

    [Fact]
    public void Impls_Happy_UsesImplementationsLabel()
    {
        var items = new[] { new SymbolResult("Foo", "Ns.Foo", "Class", "/a/F.cs", 1, 1, null, "Ns") };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteImpls(items, "IFoo", "S.sln");
        Assert.Contains("1 implementation of IFoo in S.sln", cap.GetOutput());
    }

    [Fact]
    public void Callers_Happy_RendersFromNodes()
    {
        var nodes = new List<CallGraphNode>
        {
            new("root", "Demo.Target", "method", "/a/T.cs", 5, 5, null, null),
            new("a", "Demo.CallerA", "method", "/a/A.cs", 10, 4, null, null),
            new("b", "Demo.CallerB", "method", "/a/B.cs", 20, 4, null, null)
        };
        var edges = new List<CallGraphEdge>
        {
            new("a", "root", "calls", true),
            new("b", "root", "calls", false)
        };
        var graph = new CallGraphResult("root", "incoming", 1, nodes, edges);
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteCallers(graph, "Demo.Target", "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("2 callers of Demo.Target in S.sln", text);
        Assert.Contains("/a/A.cs:10:4: method Demo.CallerA", text);
        Assert.Contains("/a/B.cs:20:4: method Demo.CallerB", text);
    }

    [Fact]
    public void Symbols_Paging_HintIncluded()
    {
        var page = new SymbolsResultPage(new[]
        {
            new SymbolResult("Foo", "N.Foo", "class", "/a/Foo.cs", 1, 1, null, "N")
        }, HasMore: true);
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteSymbols(page, "Foo*", "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("1 symbol matching Foo* in S.sln", text);
        Assert.Contains("more available", text);
        Assert.Contains("/a/Foo.cs:1:1: class N.Foo", text);
    }

    [Fact]
    public void Symbols_NoPaging_NoHint()
    {
        var page = new SymbolsResultPage(new[]
        {
            new SymbolResult("Foo", "N.Foo", "class", "/a/Foo.cs", 1, 1, null, "N"),
            new SymbolResult("Bar", "N.Bar", "class", "/a/Bar.cs", 2, 1, null, "N")
        }, HasMore: false);
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteSymbols(page, "*", "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("2 symbols matching * in S.sln", text);
        Assert.DoesNotContain("more available", text);
    }

    [Fact]
    public void Unused_Happy_HeaderAndRows()
    {
        var items = new[]
        {
            new UnusedCandidateResult("N.Foo", "class", "/a/F.cs", 1, 1, "Proj", "no references", 0.95)
        };
        var summary = new UnusedScanSummary("class", "Proj", PublicOnly: true, IncludeGenerated: false, Scanned: 42, Items: items);
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteUnused(summary, "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("1 unused class candidate (scanned 42, publicOnly=true, includeGenerated=false) in S.sln", text);
        Assert.Contains("/a/F.cs:1:1: class N.Foo [confidence=0.95] (no references)", text);
    }

    [Fact]
    public void NoColumnPadding_AcrossRows()
    {
        var items = new[]
        {
            new ReferenceResult("/short.cs", 1, 1, "x"),
            new ReferenceResult("/much/longer/path/file.cs", 200, 30, "y")
        };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRefs(items, "T", "S.sln");
        var lines = cap.GetOutput().Split(Environment.NewLine);
        // Row 2 (after header+blank): /short.cs:1:1: x — must not have padding spaces
        Assert.Equal("/short.cs:1:1: x", lines[2]);
        Assert.Equal("/much/longer/path/file.cs:200:30: y", lines[3]);
    }
}
