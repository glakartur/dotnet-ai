using System.Net.Sockets;
using System.Text;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public sealed class DaemonRetryOnTransportFailureTests
{
    [Fact]
    public async Task SendWithRetryCore_TransportFailureThenSuccess_RestartsAndReturnsResponse()
    {
        await using var listener = await TrivialListener.StartAsync();

        var clientsObtained = new List<DaemonClient>();
        var sendAttempts = 0;
        var restartNotices = 0;
        DaemonClient? firstClient = null;
        DaemonClient? secondClient = null;

        var result = await CommandHelpers.SendWithRetryCoreAsync(
            connect: async () =>
            {
                var c = (await DaemonClient.TryConnectAsync(listener.SolutionPath))!;
                clientsObtained.Add(c);
                return c;
            },
            send: _ =>
            {
                sendAttempts++;
                if (sendAttempts == 1)
                {
                    firstClient = clientsObtained[0];
                    throw new DaemonTransportException(new ErrorInfo("DAEMON_TRANSPORT_FAILED", "boom"));
                }

                secondClient = clientsObtained[^1];
                return Task.FromResult(new DaemonResponse(
                    Id: "1",
                    Status: DaemonResponseStatus.Ok,
                    Result: new { ok = true }));
            },
            onRestart: () => restartNotices++);

        Assert.Equal(2, sendAttempts);
        Assert.Equal(1, restartNotices);
        Assert.Equal(2, clientsObtained.Count);
        Assert.NotSame(firstClient, secondClient);
        Assert.NotNull(result.Response);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SendWithRetryCore_ValidationErrorOnFirstSend_DoesNotRetry()
    {
        await using var listener = await TrivialListener.StartAsync();

        var sendAttempts = 0;
        var restartNotices = 0;
        var connectCalls = 0;

        var result = await CommandHelpers.SendWithRetryCoreAsync(
            connect: async () =>
            {
                connectCalls++;
                return (await DaemonClient.TryConnectAsync(listener.SolutionPath))!;
            },
            send: _ =>
            {
                sendAttempts++;
                throw new DaemonClientValidationException(new ErrorInfo("INVALID_PARAMS", "bad"));
            },
            onRestart: () => restartNotices++);

        Assert.Equal(1, sendAttempts);
        Assert.Equal(0, restartNotices);
        Assert.Equal(1, connectCalls);
        Assert.NotNull(result.Error);
        Assert.Equal("INVALID_PARAMS", result.Error!.Code);
    }

    [Fact]
    public async Task SendWithRetryCore_BothAttemptsTransportFail_StopsAfterExactlyTwoAttempts()
    {
        await using var listener = await TrivialListener.StartAsync();

        var sendAttempts = 0;
        var restartNotices = 0;

        var result = await CommandHelpers.SendWithRetryCoreAsync(
            connect: async () => (await DaemonClient.TryConnectAsync(listener.SolutionPath))!,
            send: _ =>
            {
                sendAttempts++;
                throw new DaemonTransportException(new ErrorInfo("DAEMON_TRANSPORT_FAILED", "boom"));
            },
            onRestart: () => restartNotices++);

        Assert.Equal(2, sendAttempts);
        Assert.Equal(1, restartNotices);
        Assert.NotNull(result.Error);
        Assert.Equal("DAEMON_TRANSPORT_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task SendWithRetryCore_RestartConnectFails_StopsWithoutSecondSend()
    {
        await using var listener = await TrivialListener.StartAsync();

        var sendAttempts = 0;
        var connectCalls = 0;
        var restartNotices = 0;

        var result = await CommandHelpers.SendWithRetryCoreAsync(
            connect: async () =>
            {
                connectCalls++;
                if (connectCalls == 1)
                    return (await DaemonClient.TryConnectAsync(listener.SolutionPath))!;

                throw new DaemonClientValidationException(new ErrorInfo("DAEMON_STARTUP_FAILED", "fail"));
            },
            send: _ =>
            {
                sendAttempts++;
                throw new DaemonTransportException(new ErrorInfo("DAEMON_TRANSPORT_FAILED", "boom"));
            },
            onRestart: () => restartNotices++);

        Assert.Equal(1, sendAttempts);
        Assert.Equal(1, restartNotices);
        Assert.Equal(2, connectCalls);
        Assert.NotNull(result.Error);
        Assert.Equal("DAEMON_STARTUP_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task SendWithRetryCore_SuccessOnFirstAttempt_DoesNotRestart()
    {
        await using var listener = await TrivialListener.StartAsync();

        var sendAttempts = 0;
        var restartNotices = 0;

        var result = await CommandHelpers.SendWithRetryCoreAsync(
            connect: async () => (await DaemonClient.TryConnectAsync(listener.SolutionPath))!,
            send: _ =>
            {
                sendAttempts++;
                return Task.FromResult(new DaemonResponse(
                    Id: "1",
                    Status: DaemonResponseStatus.Ok,
                    Result: new { ok = true }));
            },
            onRestart: () => restartNotices++);

        Assert.Equal(1, sendAttempts);
        Assert.Equal(0, restartNotices);
        Assert.Null(result.Error);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public async Task SendWithRetryCore_ValidationErrorOnSecondAttempt_NotRetriedFurther()
    {
        await using var listener = await TrivialListener.StartAsync();

        var sendAttempts = 0;
        var restartNotices = 0;

        var result = await CommandHelpers.SendWithRetryCoreAsync(
            connect: async () => (await DaemonClient.TryConnectAsync(listener.SolutionPath))!,
            send: _ =>
            {
                sendAttempts++;
                if (sendAttempts == 1)
                    throw new DaemonTransportException(new ErrorInfo("DAEMON_TRANSPORT_FAILED", "boom"));
                throw new DaemonClientValidationException(new ErrorInfo("INVALID_PARAMS", "bad"));
            },
            onRestart: () => restartNotices++);

        Assert.Equal(2, sendAttempts);
        Assert.Equal(1, restartNotices);
        Assert.NotNull(result.Error);
        Assert.Equal("INVALID_PARAMS", result.Error!.Code);
    }

    private sealed class TrivialListener : IAsyncDisposable
    {
        private readonly Socket _socket;
        public string SolutionPath { get; }
        public string SocketPath { get; }

        private TrivialListener(Socket socket, string solutionPath, string socketPath)
        {
            _socket = socket;
            SolutionPath = solutionPath;
            SocketPath = socketPath;
        }

        public static Task<TrivialListener> StartAsync()
        {
            var solutionPath = Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
            var socketPath = DaemonClient.GetSocketPath(solutionPath);
            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
            if (File.Exists(socketPath))
                File.Delete(socketPath);

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            socket.Listen(8);

            // We don't actually accept — TryConnectAsync only needs the listener to exist.
            // Accepted connections accumulate in the kernel backlog; that is fine for these tests
            // because the send-delegate is fully mocked.
            return Task.FromResult(new TrivialListener(socket, solutionPath, socketPath));
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            _socket.Dispose();

            try
            {
                if (File.Exists(SocketPath))
                    File.Delete(SocketPath);
            }
            catch
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
