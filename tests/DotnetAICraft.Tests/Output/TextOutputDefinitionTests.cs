using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class TextOutputDefinitionTests
{
    [Fact]
    public void Happy_RendersFullNameThenKeyValues()
    {
        var def = new DefinitionResult("Demo.Service.DoWork", "method", "/a/Service.cs", 12, 5, "Service", "Demo");
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteDefinition(def, "S.sln");
        var lines = cap.GetOutput().Split(Environment.NewLine);
        Assert.Equal("Demo.Service.DoWork", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("Kind: method", lines[2]);
        Assert.Equal("Location: /a/Service.cs:12:5", lines[3]);
    }

    [Fact]
    public void MissingLocation_OmitsLocationRow()
    {
        var def = new DefinitionResult("System.String", "class", null, null, null, null, "System");
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteDefinition(def, "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("Kind: class", text);
        Assert.DoesNotContain("Location:", text);
    }
}
