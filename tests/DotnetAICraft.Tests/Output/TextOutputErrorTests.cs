using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class TextOutputErrorTests
{
    [Fact]
    public void Hint_RenderedAsSecondLine()
    {
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteError("SOLUTION_UNAVAILABLE", "Solution is currently unavailable.",
            new { hint = "Run 'server reload' or fix the solution/project files and retry." });
        var lines = cap.GetOutput().Split(Environment.NewLine);
        Assert.Equal("error SOLUTION_UNAVAILABLE: Solution is currently unavailable.", lines[0]);
        Assert.Equal("hint: Run 'server reload' or fix the solution/project files and retry.", lines[1]);
    }

    [Fact]
    public void NoDetails_SingleLine()
    {
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteError("X", "y", null);
        Assert.Equal($"error X: y{Environment.NewLine}", cap.GetOutput());
    }

    [Fact]
    public void StructuredDetailsWithoutHint_IndentedKeyValueRows()
    {
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteError("X", "y", new { command = "refs", attempted = 3 });
        var text = cap.GetOutput();
        Assert.Contains("error X: y", text);
        Assert.Contains("  command: refs", text);
        Assert.Contains("  attempted: 3", text);
    }
}
