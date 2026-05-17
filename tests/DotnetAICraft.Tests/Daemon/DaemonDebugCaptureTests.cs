using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DaemonDebugCaptureTests
{
    private static DaemonServer NewServer()
        => new DaemonServer(Path.Combine(Path.GetTempPath(), "no-such.sln"));

    [Fact]
    public async Task DispatchAsync_WithDebugTrue_AttachesDebugLinesArray()
    {
        await using var server = NewServer();
        var req = new DaemonRequest(
            Id: "req-1",
            Command: "status",
            Params: null,
            Debug: true);

        var response = await server.DispatchAsync(req, CancellationToken.None);

        Assert.NotNull(response.Debug);
        var lines = Assert.IsType<string[]>(response.Debug);
        Assert.Contains(lines, l => l.Contains("DispatchAsync begin") && l.Contains("command=status"));
        Assert.Contains(lines, l => l.Contains("DispatchAsync end") && l.Contains("command=status"));
    }

    [Fact]
    public async Task DispatchAsync_WithDebugNull_LeavesDebugNull()
    {
        await using var server = NewServer();
        var req = new DaemonRequest(
            Id: "req-2",
            Command: "status",
            Params: null,
            Debug: null);

        var response = await server.DispatchAsync(req, CancellationToken.None);

        Assert.Null(response.Debug);
    }

    [Fact]
    public async Task DispatchAsync_ConcurrentRequests_ScopeDebugIndependently()
    {
        await using var server = NewServer();
        var withDebug = new DaemonRequest(
            Id: "with-debug",
            Command: "status",
            Params: null,
            Debug: true);
        var noDebug = new DaemonRequest(
            Id: "no-debug",
            Command: "status",
            Params: null,
            Debug: null);

        var taskA = Task.Run(() => server.DispatchAsync(withDebug, CancellationToken.None));
        var taskB = Task.Run(() => server.DispatchAsync(noDebug, CancellationToken.None));
        var responses = await Task.WhenAll(taskA, taskB);

        var withDebugResponse = responses[0];
        var noDebugResponse = responses[1];

        var lines = Assert.IsType<string[]>(withDebugResponse.Debug);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains("id=with-debug", l));
        Assert.DoesNotContain(lines, l => l.Contains("id=no-debug"));

        Assert.Null(noDebugResponse.Debug);
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrows_StillPopulatesDebugForRequestedRun()
    {
        await using var server = NewServer();
        var req = new DaemonRequest(
            Id: "req-bad",
            Command: "does-not-exist",
            Params: null,
            Debug: true);

        var response = await server.DispatchAsync(req, CancellationToken.None);

        Assert.Equal(DaemonResponseStatus.Error, response.Status);
        var lines = Assert.IsType<string[]>(response.Debug);
        Assert.Contains(lines, l => l.Contains("DispatchAsync begin"));
        Assert.Contains(lines, l => l.Contains("DispatchAsync end"));
    }
}
