using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class JsonFormatParityTests
{
    [Fact]
    public void Write_Refs_PrettyPrintedJson_ByteExact()
    {
        var items = new[]
        {
            new ReferenceResult("/a/F.cs", 10, 4, "ctx")
        };
        using var cap = ConsoleOutputCapture.Start();
        JsonOutput.Write(items);
        var expected =
            "[\n" +
            "  {\n" +
            "    \"file\": \"/a/F.cs\",\n" +
            "    \"line\": 10,\n" +
            "    \"col\": 4,\n" +
            "    \"context\": \"ctx\"\n" +
            "  }\n" +
            "]\n";
        var actual = cap.GetOutput().Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteError_NoDetails_ByteExact()
    {
        using var cap = ConsoleOutputCapture.Start();
        JsonOutput.WriteError("ERR", "msg");
        var expected =
            "{\n" +
            "  \"error\": {\n" +
            "    \"code\": \"ERR\",\n" +
            "    \"message\": \"msg\"\n" +
            "  }\n" +
            "}\n";
        var actual = cap.GetOutput().Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteError_WithHint_ByteExact()
    {
        using var cap = ConsoleOutputCapture.Start();
        JsonOutput.WriteError("ERR", "msg", new { hint = "do this" });
        var expected =
            "{\n" +
            "  \"error\": {\n" +
            "    \"code\": \"ERR\",\n" +
            "    \"message\": \"msg\",\n" +
            "    \"details\": {\n" +
            "      \"hint\": \"do this\"\n" +
            "    }\n" +
            "  }\n" +
            "}\n";
        var actual = cap.GetOutput().Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteWithSolutionRoot_ArrayResult_WrapsAsItems()
    {
        var items = new[] { new ReferenceResult("Foo/Bar.cs", 10, 4, "ctx") };
        using var cap = ConsoleOutputCapture.Start();
        JsonOutput.WriteWithSolutionRoot("/repo", items);
        var actual = cap.GetOutput().Replace("\r\n", "\n");
        Assert.Contains("\"solutionRoot\": \"/repo\"", actual);
        Assert.Contains("\"items\":", actual);
        Assert.Contains("\"file\": \"Foo/Bar.cs\"", actual);
        var solutionRootIndex = actual.IndexOf("\"solutionRoot\"", StringComparison.Ordinal);
        var itemsIndex = actual.IndexOf("\"items\"", StringComparison.Ordinal);
        Assert.True(solutionRootIndex < itemsIndex);
    }

    [Fact]
    public void WriteWithSolutionRoot_ObjectResult_PrependsSolutionRoot()
    {
        var page = new SymbolsResultPage(
            new[] { new SymbolResult("X", "Demo.X", "class", "X.cs", 1, 1, null, "Demo") },
            HasMore: true);
        using var cap = ConsoleOutputCapture.Start();
        JsonOutput.WriteWithSolutionRoot("/repo", page);
        var actual = cap.GetOutput().Replace("\r\n", "\n");
        Assert.Contains("\"solutionRoot\": \"/repo\"", actual);
        Assert.Contains("\"items\":", actual);
        Assert.Contains("\"hasMore\": true", actual);
        var solutionRootIndex = actual.IndexOf("\"solutionRoot\"", StringComparison.Ordinal);
        var itemsIndex = actual.IndexOf("\"items\"", StringComparison.Ordinal);
        Assert.True(solutionRootIndex < itemsIndex);
    }

    [Fact]
    public void WriteSolutionRootHeader_TextFormat_WritesHeaderAndBlankLine()
    {
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteSolutionRootHeader("/repo");
        var lines = cap.GetOutput().Split(Environment.NewLine);
        Assert.Equal("SolutionRoot: /repo", lines[0]);
        Assert.Equal("", lines[1]);
    }
}
