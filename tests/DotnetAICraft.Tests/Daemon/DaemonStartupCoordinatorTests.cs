using System.Diagnostics;
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

    private static string CreateUniqueSolutionPath()
        => Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
}
