using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public sealed class DaemonTransportFailureTests
{
    [Fact]
    public async Task SendAsync_WhenListenerShutsDownBeforeSend_ThrowsDaemonTransportException()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        var fake = await TransportFailureFakeDaemon.StartAsync(socketPath, TransportFailureFakeDaemon.Mode.DropOnAccept);
        DaemonClient? client = null;
        try
        {
            client = await DaemonClient.TryConnectAsync(solutionPath);
            Assert.NotNull(client);

            await fake.StopAcceptingAsync();

            var ex = await Assert.ThrowsAsync<DaemonTransportException>(
                () => client!.SendAsync("symbols"));

            Assert.Equal("DAEMON_TRANSPORT_FAILED", ex.Error.Code);
            using var details = JsonDocument.Parse(JsonSerializer.Serialize(ex.Error.Details));
            Assert.Equal("symbols", details.RootElement.GetProperty("command").GetString());
            Assert.False(string.IsNullOrEmpty(details.RootElement.GetProperty("innerType").GetString()));
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
            await fake.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendAsync_WhenServerClosesAfterReadingRequest_SurfacesIncompleteResponse()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        var fake = await TransportFailureFakeDaemon.StartAsync(socketPath, TransportFailureFakeDaemon.Mode.ReadThenCloseGracefully);
        DaemonClient? client = null;
        try
        {
            client = await DaemonClient.TryConnectAsync(solutionPath);
            Assert.NotNull(client);

            // EOF read returns null → existing validation-style envelope; not a transport fault.
            var ex = await Assert.ThrowsAsync<DaemonClientValidationException>(
                () => client!.SendAsync("symbols"));

            Assert.Equal("DAEMON_RESPONSE_INCOMPLETE", ex.Error.Code);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
            await fake.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendAsync_WhenServerReturnsValidationError_DoesNotReclassifyAsTransportFailure()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        var fake = await TransportFailureFakeDaemon.StartAsync(socketPath, TransportFailureFakeDaemon.Mode.RespondInvalidParams);
        DaemonClient? client = null;
        try
        {
            client = await DaemonClient.TryConnectAsync(solutionPath);
            Assert.NotNull(client);

            var response = await client!.SendAsync("symbols");
            Assert.Equal(DaemonResponseStatus.Error, response.Status);
            Assert.NotNull(response.Error);
            Assert.Equal("INVALID_PARAMS", response.Error!.Code);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
            await fake.DisposeAsync();
        }
    }

    private static string CreateUniqueSolutionPath()
        => Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");

    private sealed class TransportFailureFakeDaemon : IAsyncDisposable
    {
        public enum Mode
        {
            DropOnAccept,
            ReadThenCloseGracefully,
            RespondInvalidParams
        }

        private readonly Socket _listener;
        private readonly string _socketPath;
        private readonly Mode _mode;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private TransportFailureFakeDaemon(Socket listener, string socketPath, Mode mode)
        {
            _listener = listener;
            _socketPath = socketPath;
            _mode = mode;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static Task<TransportFailureFakeDaemon> StartAsync(string socketPath, Mode mode)
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            socket.Listen(8);
            return Task.FromResult(new TransportFailureFakeDaemon(socket, socketPath, mode));
        }

        public async Task StopAcceptingAsync()
        {
            _cts.Cancel();
            try
            {
                _listener.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            _listener.Dispose();

            try
            {
                await _acceptLoop;
            }
            catch
            {
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    Socket accepted;
                    try
                    {
                        accepted = await _listener.AcceptAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    _ = Task.Run(() => HandleClientAsync(accepted));
                }
            }
            catch
            {
            }
        }

        private async Task HandleClientAsync(Socket socket)
        {
            try
            {
                if (_mode == Mode.DropOnAccept)
                {
                    socket.Close(0);
                    return;
                }

                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_cts.Token);

                if (_mode == Mode.ReadThenCloseGracefully || line is null)
                    return;

                if (_mode == Mode.RespondInvalidParams)
                {
                    string id;
                    using (var doc = JsonDocument.Parse(line))
                    {
                        id = doc.RootElement.TryGetProperty("id", out var idElem)
                            ? idElem.GetString() ?? string.Empty
                            : string.Empty;
                    }

                    var response = new DaemonResponse(
                        Id: id,
                        Status: DaemonResponseStatus.Error,
                        Error: new ErrorInfo("INVALID_PARAMS", "fake invalid params"));

                    using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(DotnetAICraft.Output.JsonOutput.Serialize(response));
                }
            }
            catch
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_cts.IsCancellationRequested)
                await StopAcceptingAsync();

            _cts.Dispose();

            try
            {
                if (File.Exists(_socketPath))
                    File.Delete(_socketPath);
            }
            catch
            {
            }
        }
    }
}
