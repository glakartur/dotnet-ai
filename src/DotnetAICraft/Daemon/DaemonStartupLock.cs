using System.Security.Cryptography;
using System.Text;
using DotnetAICraft.Models;

namespace DotnetAICraft.Daemon;

public sealed class DaemonStartupLock : IDisposable, IAsyncDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    public string SolutionPath { get; }
    public string LockPath { get; }

    private DaemonStartupLock(string solutionPath, string lockPath, FileStream stream)
    {
        SolutionPath = solutionPath;
        LockPath = lockPath;
        _stream = stream;
    }

    public static async Task<DaemonStartupLock> AcquireAsync(
        string solutionPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var normalizedSolutionPath = Path.GetFullPath(solutionPath);
        var lockPath = GetLockPath(normalizedSolutionPath);
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastIoException = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(lockPath))
                {
                    var attrs = File.GetAttributes(lockPath);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw new DaemonStartupException(new ErrorInfo(
                            "DAEMON_STARTUP_LOCK_FAILED",
                            "Could not acquire startup lock because lock path is a reparse point.",
                            new { stage = "lock", lockPath }));
                    }
                }

                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                return new DaemonStartupLock(normalizedSolutionPath, lockPath, stream);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DaemonStartupException(new ErrorInfo(
                    "DAEMON_STARTUP_LOCK_FAILED",
                    "Could not acquire startup lock due to filesystem permissions.",
                    new { stage = "lock", lockPath, reason = ex.Message }));
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new DaemonStartupException(new ErrorInfo(
                    "DAEMON_STARTUP_LOCK_FAILED",
                    "Could not acquire startup lock because lock directory is unavailable.",
                    new { stage = "lock", lockPath, reason = ex.Message }));
            }
            catch (IOException ex)
            {
                lastIoException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        throw new DaemonStartupException(new ErrorInfo(
            "DAEMON_STARTUP_LOCK_TIMEOUT",
            "Timed out waiting for startup lock.",
            new
            {
                stage = "lock",
                timeoutMs = (long)timeout.TotalMilliseconds,
                lockPath,
                reason = lastIoException?.Message
            }));
    }

    public static string GetLockPath(string solutionPath)
    {
        var normalized = Path.GetFullPath(solutionPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        return Path.Combine(DaemonClient.GetRuntimeDirectory(), $"dotnet-aicraft-{hash}.startup.lock");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stream.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class DaemonStartupException : Exception
{
    public ErrorInfo Error { get; }

    public DaemonStartupException(ErrorInfo error)
        : base(error.Message)
    {
        Error = error;
    }
}
