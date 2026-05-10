using DotnetAICraft.Models;

namespace DotnetAICraft.Daemon;

public enum DaemonStartupOutcomeType
{
    AttachedExisting,
    StartedNew,
    Failed
}

public sealed record DaemonStartupOutcome(
    DaemonStartupOutcomeType Type,
    DaemonClient? Client,
    ErrorInfo? Error,
    string Stage)
{
    public static DaemonStartupOutcome Attached(DaemonClient client)
        => new(DaemonStartupOutcomeType.AttachedExisting, client, null, "attach");

    public static DaemonStartupOutcome Started(DaemonClient client)
        => new(DaemonStartupOutcomeType.StartedNew, client, null, "ready");

    public static DaemonStartupOutcome Failed(ErrorInfo error, string stage)
        => new(DaemonStartupOutcomeType.Failed, null, error, stage);
}

public enum DaemonServerStartDecisionType
{
    AttachedExisting,
    StartNew,
    Failed
}

public sealed record DaemonServerStartDecision(
    DaemonServerStartDecisionType Type,
    DaemonStartupLock? StartupLock,
    ErrorInfo? Error)
{
    public static DaemonServerStartDecision Attached()
        => new(DaemonServerStartDecisionType.AttachedExisting, null, null);

    public static DaemonServerStartDecision StartNew(DaemonStartupLock startupLock)
        => new(DaemonServerStartDecisionType.StartNew, startupLock, null);

    public static DaemonServerStartDecision Failed(ErrorInfo error)
        => new(DaemonServerStartDecisionType.Failed, null, error);
}

public static class DaemonStartupCoordinator
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan LivenessProbeDelay = TimeSpan.FromMilliseconds(100);
    private const int LivenessProbeAttempts = 3;

    public static async Task<DaemonStartupOutcome> ConnectOrStartAsync(
        string solutionPath,
        DaemonIdleTimeoutSetting? idleTimeout,
        TimeSpan? readyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveReadyTimeout = readyTimeout ?? ReadyTimeout;

        var existing = await DaemonClient.TryConnectAsync(solutionPath);
        if (existing is not null)
        {
            try
            {
                if (idleTimeout is not null)
                    await DaemonClient.ApplyIdleTimeoutAsync(existing, idleTimeout);

                return DaemonStartupOutcome.Attached(existing);
            }
            catch
            {
                await existing.DisposeAsync();
                throw;
            }
        }

        var startupProcess = await DaemonClient.StartDaemonProcessAsync(solutionPath, idleTimeout);

        var (started, exitCode) = await DaemonClient.WaitForDaemonAsync(solutionPath, effectiveReadyTimeout, startupProcess);
        if (started is null)
        {
            if (exitCode is not null)
            {
                return DaemonStartupOutcome.Failed(new ErrorInfo(
                    "DAEMON_STARTUP_PROCESS_EXITED",
                    "Daemon process exited before becoming ready.",
                    new { stage = "start", exitCode, timeoutMs = (long)effectiveReadyTimeout.TotalMilliseconds }), "start");
            }

            return DaemonStartupOutcome.Failed(new ErrorInfo(
                "DAEMON_STARTUP_READY_TIMEOUT",
                $"Daemon did not become ready within {effectiveReadyTimeout.TotalSeconds:0}s.",
                new { stage = "ready", timeoutMs = (long)effectiveReadyTimeout.TotalMilliseconds }), "ready");
        }

        return DaemonStartupOutcome.Started(started);
    }

    public static async Task<DaemonServerStartDecision> PrepareServerStartAsync(
        string solutionPath,
        TimeSpan? lockTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveLockTimeout = lockTimeout ?? LockTimeout;

        try
        {
            var startupLock = await DaemonStartupLock.AcquireAsync(solutionPath, effectiveLockTimeout, cancellationToken);

            try
            {
                var existingDetected = await IsDaemonActiveAsync(solutionPath, cancellationToken);
                if (existingDetected)
                {
                    await startupLock.DisposeAsync();
                    return DaemonServerStartDecision.Attached();
                }

                var staleCleanup = TryDeleteStaleSocket(solutionPath, out var staleCleanupError);
                if (!staleCleanup)
                {
                    await startupLock.DisposeAsync();
                    return DaemonServerStartDecision.Failed(staleCleanupError!);
                }

                return DaemonServerStartDecision.StartNew(startupLock);
            }
            catch
            {
                await startupLock.DisposeAsync();
                throw;
            }
        }
        catch (DaemonStartupException ex)
        {
            return DaemonServerStartDecision.Failed(ex.Error);
        }
    }

    private static async Task<bool> IsDaemonActiveAsync(string solutionPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < LivenessProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = await DaemonClient.TryConnectAsync(solutionPath);
            if (existing is not null)
            {
                await existing.DisposeAsync();
                return true;
            }

            if (attempt + 1 < LivenessProbeAttempts)
                await Task.Delay(LivenessProbeDelay, cancellationToken);
        }

        return false;
    }

    private static bool TryDeleteStaleSocket(string solutionPath, out ErrorInfo? error)
    {
        error = null;
        var socketPath = DaemonClient.GetSocketPath(solutionPath);

        if (!File.Exists(socketPath))
            return true;

        try
        {
            var attrs = File.GetAttributes(socketPath);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                error = new ErrorInfo(
                    "DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE",
                    "Refusing to delete stale daemon socket because path is a reparse point.",
                    new { stage = "liveness", socketPath });
                return false;
            }

            File.Delete(socketPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = new ErrorInfo(
                "DAEMON_STARTUP_STALE_SOCKET_DELETE_FAILED",
                "Failed to remove stale daemon socket.",
                new { stage = "liveness", socketPath, reason = ex.Message });
            return false;
        }
    }
}
