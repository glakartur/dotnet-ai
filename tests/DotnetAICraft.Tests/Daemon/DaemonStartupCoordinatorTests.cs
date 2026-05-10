using System.Diagnostics;
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
        await first.DisposeAsync();

        await using var second = await DaemonStartupLock.AcquireAsync(solutionPath, TimeSpan.FromSeconds(1));
        Assert.Equal(Path.GetFullPath(solutionPath), second.SolutionPath);
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
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                ArgumentList = { "-lc", "exit 7" },
                UseShellExecute = false,
                CreateNoWindow = true
            }
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
    public void GetLockPath_UsesRuntimeDirectory()
    {
        var lockPath = DaemonStartupLock.GetLockPath(CreateUniqueSolutionPath());
        Assert.StartsWith(DaemonClient.GetRuntimeDirectory(), lockPath, StringComparison.Ordinal);
    }

    private static string CreateUniqueSolutionPath()
        => Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
}
