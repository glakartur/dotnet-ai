using DotnetAICraft.Diagnostics;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Diagnostics;

[Collection("Console output")]
public class DebugLogSinkTests
{
    [Fact]
    public void BeginCapture_CollectsLinesWritten_WhileGlobalVerboseDisabled()
    {
        DebugLog.Configure(false);
        using var errCap = ConsoleErrorCapture.Start();

        string[] captured;
        using (var scope = DebugLog.BeginCapture())
        {
            DebugLog.Write("server", "hello");
            captured = scope.GetLines();
        }

        Assert.Single(captured);
        Assert.Contains("[server] hello", captured[0]);
        Assert.Matches(@"^\[dotnet-aicraft debug .+\] \[server\] hello$", captured[0]);
        Assert.Equal(string.Empty, errCap.GetOutput());
    }

    [Fact]
    public void BeginCapture_AndGlobalVerbose_BothEmitWithoutDeduplication()
    {
        DebugLog.Configure(true);
        try
        {
            using var errCap = ConsoleErrorCapture.Start();

            string[] captured;
            using (var scope = DebugLog.BeginCapture())
            {
                DebugLog.Write("server", "both");
                captured = scope.GetLines();
            }

            Assert.Single(captured);
            var stderr = errCap.GetOutput();
            Assert.Contains("[server] both", stderr);
        }
        finally
        {
            DebugLog.Configure(false);
        }
    }

    [Fact]
    public async Task BeginCapture_IsolatesParallelAsyncContexts()
    {
        DebugLog.Configure(false);

        async Task<string[]> RunCaptureAsync(string tag)
        {
            await Task.Yield();
            using var scope = DebugLog.BeginCapture();
            for (var i = 0; i < 5; i++)
            {
                await Task.Yield();
                DebugLog.Write("server", $"{tag}-{i}");
            }
            return scope.GetLines();
        }

        var taskA = Task.Run(() => RunCaptureAsync("A"));
        var taskB = Task.Run(() => RunCaptureAsync("B"));
        var resultA = await taskA;
        var resultB = await taskB;

        Assert.Equal(5, resultA.Length);
        Assert.Equal(5, resultB.Length);
        Assert.All(resultA, l => Assert.Contains("A-", l));
        Assert.All(resultB, l => Assert.Contains("B-", l));
        Assert.DoesNotContain(resultA, l => l.Contains("B-"));
        Assert.DoesNotContain(resultB, l => l.Contains("A-"));
    }

    [Fact]
    public void NoActiveScope_WriteBehavesAsBefore()
    {
        DebugLog.Configure(false);
        using var errCap = ConsoleErrorCapture.Start();

        DebugLog.Write("server", "nothing");

        Assert.Equal(string.Empty, errCap.GetOutput());
    }

    [Fact]
    public void NestedScopes_InnerCapturesOnlyInnerWrites_OuterCapturesOnlyOuterWrites()
    {
        DebugLog.Configure(false);

        string[] outerLines;
        string[] innerLines;
        using (var outer = DebugLog.BeginCapture())
        {
            DebugLog.Write("server", "outer-before");
            using (var inner = DebugLog.BeginCapture())
            {
                DebugLog.Write("server", "inner-only");
                innerLines = inner.GetLines();
            }
            DebugLog.Write("server", "outer-after");
            outerLines = outer.GetLines();
        }

        Assert.Single(innerLines);
        Assert.Contains("inner-only", innerLines[0]);

        Assert.Equal(2, outerLines.Length);
        Assert.Contains("outer-before", outerLines[0]);
        Assert.Contains("outer-after", outerLines[1]);
        Assert.DoesNotContain(outerLines, l => l.Contains("inner-only"));
    }
}
