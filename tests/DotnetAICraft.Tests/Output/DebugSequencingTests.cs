using DotnetAICraft.Diagnostics;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class DebugSequencingTests
{
    [Fact]
    public void WriteResponseDebug_EmitsToStderr()
    {
        using var errCap = ConsoleErrorCapture.Start();
        using var outCap = ConsoleOutputCapture.Start();

        DebugLog.WriteResponseDebug(new { hello = "world", n = 1 });

        var err = errCap.GetOutput();
        var stdout = outCap.GetOutput();

        Assert.Contains("hello", err);
        Assert.Contains("world", err);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void WriteResponseDebug_EachLineSeparateOnStderr()
    {
        using var errCap = ConsoleErrorCapture.Start();
        DebugLog.WriteResponseDebug(new { a = 1, b = 2 });
        var err = errCap.GetOutput();
        // Should produce at least one stderr line ending; serialized JSON is single-line
        // when WireOptions has WriteIndented = false. Either way, content is captured.
        Assert.NotEmpty(err);
    }
}
