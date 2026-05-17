using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class DebugSequencingTests
{
    [Fact]
    public void WriteResponseDebug_EmitsLinesVerbatimToStderr()
    {
        using var errCap = ConsoleErrorCapture.Start();
        using var outCap = ConsoleOutputCapture.Start();

        DebugLog.WriteResponseDebug(new[]
        {
            "[dotnet-aicraft debug 2026-05-17T00:00:00.0000000Z] [server] one",
            "[dotnet-aicraft debug 2026-05-17T00:00:00.0000001Z] [server] two",
            "[dotnet-aicraft debug 2026-05-17T00:00:00.0000002Z] [server] three"
        });

        var stderrLines = errCap.GetOutput()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, stderrLines.Length);
        Assert.EndsWith("[server] one", stderrLines[0]);
        Assert.EndsWith("[server] two", stderrLines[1]);
        Assert.EndsWith("[server] three", stderrLines[2]);
        Assert.Equal(string.Empty, outCap.GetOutput());
    }

    [Fact]
    public void WriteResponseDebug_EmptySequenceWritesNothing()
    {
        using var errCap = ConsoleErrorCapture.Start();

        DebugLog.WriteResponseDebug(Array.Empty<string>());

        Assert.Equal(string.Empty, errCap.GetOutput());
    }

    [Fact]
    public void FlushResponseDebugToStderr_AcceptsJsonElementArray()
    {
        var serialized = DotnetAICraft.Output.JsonOutput.Serialize(new DaemonResponse(
            Id: "req",
            Status: DaemonResponseStatus.Ok,
            Result: null,
            Error: null,
            Debug: new[] { "[server] a", "[server] b" },
            Page: null,
            Meta: null));

        var round = DotnetAICraft.Output.JsonOutput.Deserialize<DaemonResponse>(serialized);
        Assert.NotNull(round);

        using var errCap = ConsoleErrorCapture.Start();
        using var outCap = ConsoleOutputCapture.Start();

        CommandHelpers.FlushResponseDebugToStderr(round!);

        var stderr = errCap.GetOutput();
        Assert.Contains("[server] a", stderr);
        Assert.Contains("[server] b", stderr);
        Assert.Equal(string.Empty, outCap.GetOutput());
    }

    [Fact]
    public void CombinedOutput_ClientAndServerLines_AppearOnStderrBeforeStdout()
    {
        DebugLog.Configure(true);
        try
        {
            using var errCap = ConsoleErrorCapture.Start();
            using var outCap = ConsoleOutputCapture.Start();

            DebugLog.Write("client", "before-send");

            var response = new DaemonResponse(
                Id: "req",
                Status: DaemonResponseStatus.Ok,
                Result: null,
                Error: null,
                Debug: new[]
                {
                    "[dotnet-aicraft debug 2026-05-17T00:00:00.0000000Z] [server] DispatchAsync begin",
                    "[dotnet-aicraft debug 2026-05-17T00:00:00.0000001Z] [server] DispatchAsync end"
                },
                Page: null,
                Meta: null);

            CommandHelpers.FlushResponseDebugToStderr(response);
            Console.WriteLine("result-payload");

            var stderr = errCap.GetOutput();
            var stdout = outCap.GetOutput();

            Assert.Contains("[client] before-send", stderr);
            Assert.Contains("[server] DispatchAsync begin", stderr);
            Assert.Contains("[server] DispatchAsync end", stderr);

            var clientIdx = stderr.IndexOf("[client] before-send", StringComparison.Ordinal);
            var firstServerIdx = stderr.IndexOf("[server] DispatchAsync begin", StringComparison.Ordinal);
            Assert.True(clientIdx >= 0 && firstServerIdx > clientIdx, "client line must appear before server lines on stderr");

            Assert.Equal("result-payload" + Environment.NewLine, stdout);
        }
        finally
        {
            DebugLog.Configure(false);
        }
    }

    [Fact]
    public void DefaultMode_NoDebug_FlushIsNoop()
    {
        using var errCap = ConsoleErrorCapture.Start();

        var response = new DaemonResponse(
            Id: "req",
            Status: DaemonResponseStatus.Ok,
            Result: null,
            Error: null,
            Debug: null,
            Page: null,
            Meta: null);

        CommandHelpers.FlushResponseDebugToStderr(response);

        Assert.Equal(string.Empty, errCap.GetOutput());
    }

    [Fact]
    public void ConfigureFromEnvironment_EnablesGlobalVerbose_LegacyManualServerStart()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_AICRAFT_DEBUG");
        DebugLog.Configure(false);
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_AICRAFT_DEBUG", "1");
            DebugLog.ConfigureFromEnvironment();

            using var errCap = ConsoleErrorCapture.Start();
            DebugLog.Write("server", "manual-start");

            var stderr = errCap.GetOutput();
            Assert.Contains("[server] manual-start", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_AICRAFT_DEBUG", previous);
            DebugLog.Configure(false);
        }
    }

    [Fact]
    public void WriteResponseDebug_LineWithEmbeddedNewlinePreserved()
    {
        using var errCap = ConsoleErrorCapture.Start();

        DebugLog.WriteResponseDebug(new[] { "first\nstill-first" });

        var raw = errCap.GetOutput();
        Assert.Contains("first\nstill-first", raw);
        var emittedLines = raw.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(emittedLines);
    }
}
