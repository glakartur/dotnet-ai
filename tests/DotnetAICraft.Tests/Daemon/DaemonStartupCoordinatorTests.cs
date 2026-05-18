using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotnetAICraft.Daemon;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public sealed class DaemonStartupCoordinatorTests
{
    [Fact]
    public async Task AcquireLock_SameSolution_SecondContenderTimesOut()
    {
        var solutionPath = CreateUniqueSolutionPath();
        await using var first = await DaemonStartupLock.AcquireAsync(solutionPath, TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<DaemonStartupException>(async () =>
            await DaemonStartupLock.AcquireAsync(solutionPath, TimeSpan.FromMilliseconds(200)));

        Assert.Equal("DAEMON_STARTUP_LOCK_TIMEOUT", ex.Error.Code);
    }

    [Fact]
    public async Task AcquireLock_ReleasesAfterDispose()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var first = await DaemonStartupLock.AcquireAsync(solutionPath, TimeSpan.FromSeconds(1));
        var lockPath = first.LockPath;
        await first.DisposeAsync();

        if (OperatingSystem.IsWindows())
            Assert.False(File.Exists(lockPath));
        else
            Assert.True(File.Exists(lockPath));

        await using var second = await DaemonStartupLock.AcquireAsync(solutionPath, TimeSpan.FromSeconds(1));
        Assert.Equal(Path.GetFullPath(solutionPath), second.SolutionPath);
    }

    [Fact]
    public async Task PrepareServerStart_WithRegularFileAtSocketPath_DeletesStalePathAndStartsNew()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        var parentDir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(parentDir);
        await File.WriteAllTextAsync(socketPath, "stale");

        var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
            solutionPath,
            lockTimeout: TimeSpan.FromSeconds(1));

        try
        {
            Assert.Equal(DaemonServerStartDecisionType.StartNew, decision.Type);
            Assert.NotNull(decision.StartupLock);
            Assert.False(File.Exists(socketPath));
        }
        finally
        {
            if (decision.StartupLock is not null)
                await decision.StartupLock.DisposeAsync();

            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public void GetLockPath_IsDeterministicPerNormalizedSolutionPath()
    {
        var basePath = Path.GetFullPath("/tmp/sample.sln");
        var withParentSegment = Path.Combine(Path.GetDirectoryName(basePath)!, ".", "sample.sln");

        var lockA = DaemonStartupLock.GetLockPath(basePath);
        var lockB = DaemonStartupLock.GetLockPath(withParentSegment);

        Assert.Equal(lockA, lockB);
    }

    [Fact]
    public async Task PrepareServerStart_WhenLocked_ReturnsFailedDecision()
    {
        var solutionPath = CreateUniqueSolutionPath();
        await using var first = await DaemonStartupLock.AcquireAsync(solutionPath, TimeSpan.FromSeconds(1));

        var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
            solutionPath,
            lockTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(DaemonServerStartDecisionType.Failed, decision.Type);
        Assert.NotNull(decision.Error);
        Assert.Equal("DAEMON_STARTUP_LOCK_TIMEOUT", decision.Error!.Code);
    }

    [Fact]
    public async Task PrepareServerStart_WhenNoDaemon_ReturnsStartNewWithLock()
    {
        var solutionPath = CreateUniqueSolutionPath();

        var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
            solutionPath,
            lockTimeout: TimeSpan.FromSeconds(1));

        try
        {
            Assert.Equal(DaemonServerStartDecisionType.StartNew, decision.Type);
            Assert.NotNull(decision.StartupLock);
        }
        finally
        {
            if (decision.StartupLock is not null)
                await decision.StartupLock.DisposeAsync();
        }
    }

    [Fact]
    public async Task WaitForDaemon_WhenProcessExited_ReturnsExitCode()
    {
        var fakeSolution = CreateUniqueSolutionPath();
        using var process = new Process
        {
            StartInfo = CreateExitProcessStartInfo(7)
        };

        process.Start();
        process.WaitForExit();

        var (client, exitCode) = await DaemonClient.WaitForDaemonAsync(
            fakeSolution,
            TimeSpan.FromMilliseconds(200),
            startupProcess: process);

        Assert.Null(client);
        Assert.Equal(7, exitCode);
    }

    private static ProcessStartInfo CreateExitProcessStartInfo(int exitCode)
    {
        var processStartInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            processStartInfo.FileName = "cmd.exe";
            processStartInfo.ArgumentList.Add("/d");
            processStartInfo.ArgumentList.Add("/c");
            processStartInfo.ArgumentList.Add($"exit /b {exitCode}");
        }
        else
        {
            processStartInfo.FileName = "/bin/sh";
            processStartInfo.ArgumentList.Add("-c");
            processStartInfo.ArgumentList.Add($"exit {exitCode}");
        }

        return processStartInfo;
    }

    [Fact]
    public async Task PrepareServerStart_WithReparseSocketPath_ReturnsFailedDecision()
    {
        if (OperatingSystem.IsWindows())
            return;

        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        var parentDir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(parentDir);

        var targetPath = Path.Combine(parentDir, $"target-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(targetPath, "target");

        try
        {
            File.CreateSymbolicLink(socketPath, targetPath);

            var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
                solutionPath,
                lockTimeout: TimeSpan.FromSeconds(1));

            Assert.Equal(DaemonServerStartDecisionType.Failed, decision.Type);
            Assert.NotNull(decision.Error);
            Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", decision.Error!.Code);

            using var details = JsonDocument.Parse(JsonSerializer.Serialize(decision.Error.Details));
            Assert.Equal("reparse-point", details.RootElement.GetProperty("artifactType").GetString());
        }
        finally
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);

            if (File.Exists(targetPath))
                File.Delete(targetPath);
        }
    }

    [Fact]
    public async Task PrepareServerStart_OnWindows_WithSafeReparseSocketPath_DeletesLinkAndStartsNew()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        var parentDir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(parentDir);

        var targetPath = Path.Combine(parentDir, $"target-{Guid.NewGuid():N}.sock");
        await File.WriteAllTextAsync(targetPath, "target");

        if (!TryCreateFileSymlink(socketPath, targetPath))
            return;

        var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
            solutionPath,
            lockTimeout: TimeSpan.FromSeconds(1));

        try
        {
            Assert.Equal(DaemonServerStartDecisionType.StartNew, decision.Type);
            Assert.NotNull(decision.StartupLock);
            Assert.False(File.Exists(socketPath));
            Assert.True(File.Exists(targetPath));

            var counters = DaemonStartupCoordinator.GetStaleSocketOutcomeCounters();
            Assert.True(counters.ReparseHeal >= 1);
        }
        finally
        {
            if (decision.StartupLock is not null)
                await decision.StartupLock.DisposeAsync();

            if (File.Exists(socketPath))
                File.Delete(socketPath);

            if (File.Exists(targetPath))
                File.Delete(targetPath);
        }
    }

    [Fact]
    public async Task PrepareServerStart_OnWindows_WithDanglingReparseSocketPath_DeletesLinkAndStartsNew()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        var socketDir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(socketDir);

        var targetPath = Path.Combine(socketDir, $"dangling-target-{Guid.NewGuid():N}.sock");

        if (!TryCreateFileSymlink(socketPath, targetPath))
            return;

        Assert.True(File.Exists(socketPath) || Directory.Exists(socketPath));

        var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
            solutionPath,
            lockTimeout: TimeSpan.FromSeconds(1));

        try
        {
            Assert.Equal(DaemonServerStartDecisionType.StartNew, decision.Type);
            Assert.NotNull(decision.StartupLock);
            Assert.False(File.Exists(socketPath));
            Assert.False(Directory.Exists(socketPath));

            var counters = DaemonStartupCoordinator.GetStaleSocketOutcomeCounters();
            Assert.True(counters.ReparseHeal >= 1);
        }
        finally
        {
            if (decision.StartupLock is not null)
                await decision.StartupLock.DisposeAsync();

            if (File.Exists(socketPath))
                File.Delete(socketPath);

            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);
        }
    }

    [Fact]
    public async Task PrepareServerStart_OnWindows_WithDirectoryReparsePoint_ReturnsUnsupportedTagReason()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        var parentDir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(parentDir);

        var targetDir = Path.Combine(parentDir, $"target-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDir);

        if (!TryCreateDirectorySymlink(socketPath, targetDir))
            return;

        try
        {
            var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
                solutionPath,
                lockTimeout: TimeSpan.FromSeconds(1));

            Assert.Equal(DaemonServerStartDecisionType.Failed, decision.Type);
            Assert.NotNull(decision.Error);

            using var details = JsonDocument.Parse(JsonSerializer.Serialize(decision.Error!.Details));
            Assert.Equal("unsupportedReparseTag", details.RootElement.GetProperty("reasonCode").GetString());
            Assert.True(Directory.Exists(socketPath));
        }
        finally
        {
            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);

            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir);
        }
    }

    [Fact]
    public async Task StaleSocketOutcomeEvent_EmitsSanitizedStructuredPayload()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        var parentDir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(parentDir);

        var recorded = new List<DaemonStartupCoordinator.StaleSocketOutcomeEvent>();
        DaemonStartupCoordinator.ResetStaleSocketOutcomeCountersForTests();

        void Handler(DaemonStartupCoordinator.StaleSocketOutcomeEvent evt) => recorded.Add(evt);
        DaemonStartupCoordinator.StaleSocketOutcomeRecorded += Handler;

        try
        {
            await File.WriteAllTextAsync(socketPath, "stale");

            var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
                solutionPath,
                lockTimeout: TimeSpan.FromSeconds(1));

            try
            {
                Assert.Equal(DaemonServerStartDecisionType.StartNew, decision.Type);
            }
            finally
            {
                if (decision.StartupLock is not null)
                    await decision.StartupLock.DisposeAsync();
            }

            var evt = Assert.Single(recorded);
            Assert.Equal("regular_heal", evt.Outcome);
            Assert.Equal("liveness", evt.Stage);
            Assert.Equal("file", evt.ArtifactType);
            Assert.Null(evt.ReasonCode);

            var counters = DaemonStartupCoordinator.GetStaleSocketOutcomeCounters();
            Assert.Equal(1, counters.RegularHeal);
            Assert.Equal(0, counters.ReparseHeal);
            Assert.Empty(counters.ReparseRejectReasons);
        }
        finally
        {
            DaemonStartupCoordinator.StaleSocketOutcomeRecorded -= Handler;

            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task PrepareServerStart_WithDirectoryAtSocketPath_ReturnsInvalidTypeWithoutDeletingPath()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(socketPath);

        try
        {
            var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(
                solutionPath,
                lockTimeout: TimeSpan.FromSeconds(1));

            Assert.Equal(DaemonServerStartDecisionType.Failed, decision.Type);
            Assert.NotNull(decision.Error);
            Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", decision.Error!.Code);

            using var details = JsonDocument.Parse(JsonSerializer.Serialize(decision.Error.Details));
            Assert.Equal("directory", details.RootElement.GetProperty("artifactType").GetString());
            Assert.True(Directory.Exists(socketPath));
        }
        finally
        {
            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);
        }
    }

    [Fact]
    public void BuildStartupProcessExitedError_WithStaleDirectory_ReturnsInvalidTypeErrorWithStartStage()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(socketPath);

        try
        {
            var error = DaemonClient.BuildStartupProcessExitedError(solutionPath, 2, TimeSpan.FromSeconds(1));

            Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", error.Code);

            using var details = JsonDocument.Parse(JsonSerializer.Serialize(error.Details));
            Assert.Equal("start", details.RootElement.GetProperty("stage").GetString());
            Assert.Equal("directory", details.RootElement.GetProperty("artifactType").GetString());
        }
        finally
        {
            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);
        }
    }

    [Fact]
    public void BuildStartupProcessExitedError_WhenNoStaleArtifact_ReturnsProcessExitedError()
    {
        var solutionPath = CreateUniqueSolutionPath();

        var error = DaemonClient.BuildStartupProcessExitedError(solutionPath, 7, TimeSpan.FromSeconds(2));

        Assert.Equal("DAEMON_STARTUP_PROCESS_EXITED", error.Code);
    }

    [Fact]
    public async Task ConnectOrStartAsync_WithDirectorySocketArtifact_ThrowsValidationWithInvalidType()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(socketPath);

        try
        {
            var ex = await Assert.ThrowsAsync<DaemonClientValidationException>(() =>
                DaemonClient.ConnectOrStartAsync(solutionPath, startTimeout: TimeSpan.FromSeconds(2)));

            Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", ex.Error.Code);
        }
        finally
        {
            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);
        }
    }

    [Fact]
    public void InvalidTypeError_OnWindows_IncludesRemediationCommands()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var solutionPath = CreateUniqueSolutionPath();
        var stalePath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(stalePath);

        try
        {
            var error = DaemonClient.BuildStartupProcessExitedError(solutionPath, 1, TimeSpan.FromSeconds(1));
            Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", error.Code);

            using var details = JsonDocument.Parse(JsonSerializer.Serialize(error.Details));
            var remediation = details.RootElement.GetProperty("remediation");
            Assert.Equal("Remove the stale artifact manually and retry daemon startup.", remediation.GetProperty("summary").GetString());
            Assert.True(remediation.GetProperty("powershell").GetString()!.Contains("Remove-Item", StringComparison.Ordinal));
            Assert.True(remediation.GetProperty("cmdDelete").GetString()!.Contains("del /f", StringComparison.OrdinalIgnoreCase));
            Assert.True(remediation.GetProperty("cmdJunction").GetString()!.Contains("rmdir", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(stalePath))
                Directory.Delete(stalePath);
        }
    }

    [Fact]
    public void GetLockPath_UsesRuntimeDirectory()
    {
        var lockPath = DaemonStartupLock.GetLockPath(CreateUniqueSolutionPath());
        Assert.StartsWith(DaemonClient.GetRuntimeDirectory(), lockPath, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidTypeError_OnWindows_RemediationEmbedsRealSocketPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var solutionPath = CreateUniqueSolutionPath();
        var stalePath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(stalePath);

        try
        {
            var error = DaemonClient.BuildStartupProcessExitedError(solutionPath, 1, TimeSpan.FromSeconds(1));

            using var details = JsonDocument.Parse(JsonSerializer.Serialize(error.Details));
            var remediation = details.RootElement.GetProperty("remediation");

            var powershell = remediation.GetProperty("powershell").GetString()!;
            var cmdDelete = remediation.GetProperty("cmdDelete").GetString()!;
            var cmdJunction = remediation.GetProperty("cmdJunction").GetString()!;

            Assert.DoesNotContain("<stale-socket-path>", powershell, StringComparison.Ordinal);
            Assert.DoesNotContain("<stale-socket-path>", cmdDelete, StringComparison.Ordinal);
            Assert.DoesNotContain("<stale-socket-path>", cmdJunction, StringComparison.Ordinal);

            Assert.Contains(stalePath, powershell, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(stalePath, cmdDelete, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(stalePath, cmdJunction, StringComparison.OrdinalIgnoreCase);

            // PowerShell -LiteralPath must use single-quoted form.
            Assert.Contains($"-LiteralPath '{stalePath}'", powershell, StringComparison.OrdinalIgnoreCase);
            // cmd variants must use double-quoted form.
            Assert.Contains($"\"{stalePath}\"", cmdDelete, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"\"{stalePath}\"", cmdJunction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(stalePath))
                Directory.Delete(stalePath);
        }
    }

    [Fact]
    public async Task TryConnectAsync_WithStaleSocketFileAndNoListener_ReturnsNull()
    {
        // A stale .sock file (left over from a crashed daemon) must not be mistaken for a live
        // daemon. TryConnectAsync returns null so the coordinator falls through to the start-new
        // path, where PrepareServerStartAsync's TryDeleteStaleSocket cleans it up.
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        try
        {
            await File.WriteAllTextAsync(socketPath, "stale");

            var client = await DaemonClient.TryConnectAsync(solutionPath);

            Assert.Null(client);
        }
        finally
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task TryConnectAsync_WithDeadListener_ReturnsNull()
    {
        // A socket file with a listener that subsequently shuts down should also produce a null
        // result from TryConnectAsync — the connect side relies on this signal to fall through
        // to startup.
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);

            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
            listener.Shutdown(SocketShutdown.Both);
            listener.Dispose();

            var client = await DaemonClient.TryConnectAsync(solutionPath);

            Assert.Null(client);
        }
        finally
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task ConnectOrStartAsync_WithExistingDaemonAndIdleTimeout_DoesNotIssueSetIdleTimeoutRequest()
    {
        var solutionPath = CreateUniqueSolutionPath();
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        await using var fake = await FakeDaemonListener.StartAsync(socketPath);

        DaemonIdleTimeoutParser.TryParse("30m", out var setting, out _);
        Assert.NotNull(setting);

        var outcome = await DaemonStartupCoordinator.ConnectOrStartAsync(
            solutionPath,
            setting,
            readyTimeout: TimeSpan.FromSeconds(5));

        try
        {
            Assert.Equal(DaemonStartupOutcomeType.AttachedExisting, outcome.Type);
            Assert.NotNull(outcome.Client);

            // Give the fake listener a brief window to observe any unexpected request.
            await Task.Delay(100);

            Assert.Empty(fake.ReceivedRequestCommands);
        }
        finally
        {
            if (outcome.Client is not null)
                await outcome.Client.DisposeAsync();
        }
    }

    private sealed class FakeDaemonListener : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly string _socketPath;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _receivedCommands = new();
        private readonly object _gate = new();
        private readonly Task _acceptLoop;

        private FakeDaemonListener(Socket listener, string socketPath)
        {
            _listener = listener;
            _socketPath = socketPath;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static Task<FakeDaemonListener> StartAsync(string socketPath)
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            socket.Listen(8);
            return Task.FromResult(new FakeDaemonListener(socket, socketPath));
        }

        public IReadOnlyList<string> ReceivedRequestCommands
        {
            get
            {
                lock (_gate)
                    return _receivedCommands.ToArray();
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
                // Swallow; the test asserts on collected state, not on listener health.
            }
        }

        private async Task HandleClientAsync(Socket socket)
        {
            try
            {
                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                    return;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("command", out var commandElement))
                {
                    var command = commandElement.GetString() ?? "<null>";
                    lock (_gate)
                        _receivedCommands.Add(command);
                }
            }
            catch
            {
                // Ignore parse / IO failures; the test does not depend on a clean response.
            }
        }

        public async ValueTask DisposeAsync()
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

    private static string CreateUniqueSolutionPath()
        => Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
