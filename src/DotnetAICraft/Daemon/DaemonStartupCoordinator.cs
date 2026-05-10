using DotnetAICraft.Models;
using System.Collections.Concurrent;
using System.Threading;

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
    private static long _regularHealCount;
    private static long _reparseHealCount;
    private static readonly ConcurrentDictionary<string, long> ReparseRejectReasonCounts = new(StringComparer.Ordinal);

    internal sealed record StaleSocketOutcomeEvent(
        string Outcome,
        string Stage,
        string ArtifactType,
        string? ReasonCode,
        string? TargetPathCategory);

    internal sealed record StaleSocketOutcomeCounters(
        long RegularHeal,
        long ReparseHeal,
        IReadOnlyDictionary<string, long> ReparseRejectReasons);

    private sealed record ReparseSafetyDecision(
        bool IsSafe,
        string ReasonCode,
        string TargetPathCategory,
        string ArtifactType);

    internal static event Action<StaleSocketOutcomeEvent>? StaleSocketOutcomeRecorded;

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
                var startupError = DaemonClient.BuildStartupProcessExitedError(
                    solutionPath,
                    exitCode.Value,
                    effectiveReadyTimeout);
                return DaemonStartupOutcome.Failed(startupError, "start");
            }

            return DaemonStartupOutcome.Failed(new ErrorInfo(
                "DAEMON_STARTUP_READY_TIMEOUT",
                $"Daemon did not become ready within {effectiveReadyTimeout.TotalSeconds:0}s.",
                new { stage = "ready", timeoutMs = (long)effectiveReadyTimeout.TotalMilliseconds }), "ready");
        }

        return DaemonStartupOutcome.Started(started);
    }

    internal static bool TryBuildInvalidStaleSocketTypeError(
        string solutionPath,
        string stage,
        out ErrorInfo? error)
    {
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        return TryBuildStaleSocketStartupErrorFromPath(socketPath, stage, out error);
    }

    internal static StaleSocketOutcomeCounters GetStaleSocketOutcomeCounters()
    {
        var reasons = ReparseRejectReasonCounts
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        return new StaleSocketOutcomeCounters(
            Interlocked.Read(ref _regularHealCount),
            Interlocked.Read(ref _reparseHealCount),
            reasons);
    }

    internal static void ResetStaleSocketOutcomeCountersForTests()
    {
        Interlocked.Exchange(ref _regularHealCount, 0);
        Interlocked.Exchange(ref _reparseHealCount, 0);
        ReparseRejectReasonCounts.Clear();
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

        if (!SocketArtifactExists(socketPath))
            return true;

        try
        {
            var attrs = File.GetAttributes(socketPath);
            if (IsRegularFileArtifact(attrs))
            {
                File.Delete(socketPath);
                RecordStaleSocketOutcome("regular_heal", "liveness", "file", null, null);
                return true;
            }

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                if (!OperatingSystem.IsWindows())
                {
                    TryBuildStaleSocketStartupErrorFromPath(socketPath, "liveness", out error);
                    return false;
                }

                var decision = EvaluateReparseSafety(socketPath, attrs);
                if (!decision.IsSafe)
                {
                    error = BuildReparseRejectError("liveness", socketPath, decision);
                    RecordStaleSocketOutcome("reparse_reject_reason", "liveness", decision.ArtifactType, decision.ReasonCode, decision.TargetPathCategory);
                    return false;
                }

                File.Delete(socketPath);
                RecordStaleSocketOutcome("reparse_heal", "liveness", decision.ArtifactType, null, decision.TargetPathCategory);
                return true;
            }

            var _ = TryBuildStaleSocketStartupErrorFromPath(socketPath, "liveness", out error);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var reasonCode = MapDeleteFailureReasonCode(ex);
            error = new ErrorInfo(
                "DAEMON_STARTUP_STALE_SOCKET_DELETE_FAILED",
                "Failed to remove stale daemon socket.",
                new
                {
                    stage = "liveness",
                    reasonCode,
                    artifactType = "file-or-reparse",
                    remediation = BuildWindowsStaleArtifactRemediation("Remove the stale artifact manually and retry daemon startup.")
                });
            return false;
        }
        catch (Exception)
        {
            error = BuildInvalidStaleSocketTypeError(
                stage: "liveness",
                socketPath,
                attrs: null,
                reasonCode: "staleArtifactCheckFailed",
                reason: "unexpectedError");
            return false;
        }
    }

    private static bool TryBuildStaleSocketStartupErrorFromPath(
        string socketPath,
        string stage,
        out ErrorInfo? error)
    {
        error = null;
        if (!SocketArtifactExists(socketPath))
            return false;

        try
        {
            var attrs = File.GetAttributes(socketPath);
            if (IsRegularFileArtifact(attrs))
                return false;

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                if (!OperatingSystem.IsWindows())
                {
                    error = BuildInvalidStaleSocketTypeError(stage, socketPath, attrs, reasonCode: "invalidArtifactType");
                    return true;
                }

                var decision = EvaluateReparseSafety(socketPath, attrs);
                if (decision.IsSafe)
                    return false;

                error = BuildReparseRejectError(stage, socketPath, decision);
                return true;
            }

            error = BuildInvalidStaleSocketTypeError(stage, socketPath, attrs, reasonCode: "invalidArtifactType");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = BuildInvalidStaleSocketTypeError(stage, socketPath, null, reasonCode: "staleArtifactCheckFailed", reason: MapDeleteFailureReasonCode(ex));
            return true;
        }
        catch (Exception)
        {
            error = BuildInvalidStaleSocketTypeError(stage, socketPath, null, reasonCode: "staleArtifactCheckFailed", reason: "unexpectedError");
            return true;
        }
    }

    private static ReparseSafetyDecision EvaluateReparseSafety(string socketPath, FileAttributes attrs)
    {
        if (!IsExpectedDaemonSocketArtifactName(socketPath))
            return new ReparseSafetyDecision(false, "artifactNameMismatch", "unknown", "reparse-point");

        if (attrs.HasFlag(FileAttributes.Directory))
            return new ReparseSafetyDecision(false, "unsupportedReparseTag", "unknown", "reparse-point");

        var socketDirectory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrWhiteSpace(socketDirectory))
            return new ReparseSafetyDecision(false, "unresolvedLinkTarget", "unknown", "reparse-point");

        if (!TryNormalizeLinkTarget(socketPath, socketDirectory, out var normalizedTarget))
            return new ReparseSafetyDecision(false, "unsupportedReparseTag", "unknown", "reparse-point");

        if (IsUncPath(normalizedTarget) || !Path.IsPathFullyQualified(normalizedTarget))
            return new ReparseSafetyDecision(false, "nonLocalTarget", "unc", "reparse-point");

        if (!IsPathUnderRoot(normalizedTarget, Path.GetTempPath()))
            return new ReparseSafetyDecision(false, "outsideTempRoot", "local", "reparse-point");

        var resolved = new FileInfo(socketPath).ResolveLinkTarget(returnFinalTarget: false);
        if (resolved is null)
        {
            if (!TryNormalizeLinkTarget(socketPath, socketDirectory, out var normalizedTargetAfterProbe))
                return new ReparseSafetyDecision(false, "unresolvedLinkTarget", "unknown", "reparse-point");

            if (!PathEquals(normalizedTargetAfterProbe, normalizedTarget))
                return new ReparseSafetyDecision(false, "targetChangedDuringValidation", "local", "reparse-point");

            return new ReparseSafetyDecision(true, "safe", "local", "reparse-point");
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(resolved.FullName);
        }
        catch
        {
            return new ReparseSafetyDecision(false, "unresolvedLinkTarget", "unknown", "reparse-point");
        }

        if (!PathEquals(resolvedPath, normalizedTarget))
            return new ReparseSafetyDecision(false, "targetChangedDuringValidation", "local", "reparse-point");

        return new ReparseSafetyDecision(true, "safe", "local", "reparse-point");
    }

    private static ErrorInfo BuildReparseRejectError(
        string stage,
        string socketPath,
        ReparseSafetyDecision decision)
    {
        var remediationMessage = decision.ReasonCode switch
        {
            "artifactNameMismatch" => "Ensure the stale artifact name matches the daemon socket naming convention before retrying.",
            "nonLocalTarget" => "Use a local (non-UNC) target for the daemon socket reparse point or remove the stale artifact manually.",
            "outsideTempRoot" => "Keep daemon socket artifacts under the current user's temp directory or remove the stale artifact manually.",
            "unsupportedReparseTag" => "Only symbolic-link stale socket artifacts are auto-cleaned. Remove this artifact manually and retry.",
            _ => "Resolve the stale reparse-point artifact manually and retry daemon startup."
        };

        return new ErrorInfo(
            "DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE",
            "Refusing to auto-clean stale daemon socket reparse point because safety policy validation failed.",
            new
            {
                stage,
                artifactName = Path.GetFileName(socketPath),
                artifactType = decision.ArtifactType,
                reasonCode = decision.ReasonCode,
                targetPathCategory = decision.TargetPathCategory,
                remediation = BuildWindowsStaleArtifactRemediation(remediationMessage)
            });
    }

    private static ErrorInfo BuildInvalidStaleSocketTypeError(
        string stage,
        string socketPath,
        FileAttributes? attrs,
        string reasonCode,
        string? reason = null)
    {
        var artifactType = attrs is null
            ? "unknown"
            : ClassifyArtifactType(attrs.Value);

        var message = attrs.HasValue && attrs.Value.HasFlag(FileAttributes.ReparsePoint)
            ? "Refusing to delete stale daemon socket because path is a reparse point."
            : "Refusing to delete stale daemon socket because path is not a regular file.";

        return new ErrorInfo(
            "DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE",
            message,
            new
            {
                stage,
                artifactName = Path.GetFileName(socketPath),
                artifactType,
                reasonCode,
                reason,
                remediation = BuildWindowsStaleArtifactRemediation("Remove the stale artifact manually and retry daemon startup.")
            });
    }

    private static object? BuildWindowsStaleArtifactRemediation(string summary)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return new
        {
            summary,
            powershell = "Remove-Item -LiteralPath <stale-socket-path> -Force",
            cmdDelete = "del /f <stale-socket-path>",
            cmdJunction = "rmdir <stale-socket-path>"
        };
    }

    private static bool IsExpectedDaemonSocketArtifactName(string socketPath)
    {
        var fileName = Path.GetFileName(socketPath);
        return fileName.StartsWith("dotnet-aicraft-", StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(".sock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    private static bool PathEquals(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return path;

        return path + Path.DirectorySeparatorChar;
    }

    private static bool TryNormalizeLinkTarget(string socketPath, string socketDirectory, out string normalizedTarget)
    {
        normalizedTarget = string.Empty;

        var linkTarget = new FileInfo(socketPath).LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
            return false;

        try
        {
            normalizedTarget = Path.IsPathFullyQualified(linkTarget)
                ? Path.GetFullPath(linkTarget)
                : Path.GetFullPath(linkTarget, socketDirectory);
            return true;
        }
        catch
        {
            normalizedTarget = string.Empty;
            return false;
        }
    }

    private static bool IsUncPath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)
                || path.StartsWith("//?/", StringComparison.Ordinal)
                || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
                || path.StartsWith("//./", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    internal static bool IsUncPathForTests(string path) => IsUncPath(path);

    private static string MapDeleteFailureReasonCode(Exception ex)
        => ex switch
        {
            UnauthorizedAccessException => "accessDenied",
            IOException => "ioError",
            _ => "deleteFailed"
        };

    private static void RecordStaleSocketOutcome(
        string outcome,
        string stage,
        string artifactType,
        string? reasonCode,
        string? targetPathCategory)
    {
        if (string.Equals(outcome, "regular_heal", StringComparison.Ordinal))
            Interlocked.Increment(ref _regularHealCount);
        else if (string.Equals(outcome, "reparse_heal", StringComparison.Ordinal))
            Interlocked.Increment(ref _reparseHealCount);
        else if (string.Equals(outcome, "reparse_reject_reason", StringComparison.Ordinal) && reasonCode is not null)
            ReparseRejectReasonCounts.AddOrUpdate(reasonCode, 1, (_, current) => current + 1);

        try
        {
            StaleSocketOutcomeRecorded?.Invoke(new StaleSocketOutcomeEvent(
                outcome,
                stage,
                artifactType,
                reasonCode,
                targetPathCategory));
        }
        catch
        {
            // Ignore subscriber failures to keep daemon startup behavior stable.
        }
    }

    private static bool SocketArtifactExists(string socketPath)
    {
        if (File.Exists(socketPath) || Directory.Exists(socketPath))
            return true;

        var parentDirectory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            return false;

        var artifactName = Path.GetFileName(socketPath);
        if (string.IsNullOrEmpty(artifactName))
            return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parentDirectory))
            {
                if (string.Equals(Path.GetFileName(entry), artifactName, comparison))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool IsRegularFileArtifact(FileAttributes attrs)
        => !attrs.HasFlag(FileAttributes.Directory) && !attrs.HasFlag(FileAttributes.ReparsePoint);

    private static string ClassifyArtifactType(FileAttributes attrs)
    {
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
            return "reparse-point";

        if (attrs.HasFlag(FileAttributes.Directory))
            return "directory";

        return "file";
    }
}
