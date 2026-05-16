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
}
