using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Daemon;

public sealed class DaemonClient : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private DaemonClient(Socket socket)
    {
        _socket = socket;
        var stream = new NetworkStream(socket, ownsSocket: false);
        _reader   = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer   = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public static async Task<DaemonClient?> TryConnectAsync(string solutionPath)
    {
        var socketPath = GetSocketPath(solutionPath);

        if (!File.Exists(socketPath)) return null;

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            return new DaemonClient(socket);
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    public static async Task<DaemonClient> ConnectOrStartAsync(
        string solutionPath,
        TimeSpan? startTimeout = null,
        string? idleTimeout = null)
    {
        if (!DaemonIdleTimeoutParser.TryParseOptional(idleTimeout, out var parsedTimeout, out var parseError))
            throw new DaemonClientValidationException(parseError!);

        var timeout = startTimeout ?? TimeSpan.FromSeconds(120);
        var outcome = await DaemonStartupCoordinator.ConnectOrStartAsync(
            solutionPath,
            parsedTimeout,
            readyTimeout: timeout);

        if (outcome.Type == DaemonStartupOutcomeType.Failed)
            throw new DaemonClientValidationException(outcome.Error ?? new ErrorInfo("DAEMON_STARTUP_FAILED", "Daemon startup failed."));

        if (outcome.Type == DaemonStartupOutcomeType.StartedNew)
        {
            Console.Error.WriteLine("[dotnet-aicraft] Starting analysis daemon (first run loads the solution)...");
            Console.Error.WriteLine("[dotnet-aicraft] Ready.");
        }

        return outcome.Client
            ?? throw new InvalidOperationException("Daemon startup coordinator returned no client.");
    }

    internal static Task<Process> StartDaemonProcessAsync(string solutionPath, DaemonIdleTimeoutSetting? idleTimeout)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current process path.");

        var args = new List<string> { "server", "start", "--solution", solutionPath };
        if (idleTimeout is not null)
        {
            var value = idleTimeout.Enabled ? idleTimeout.Normalized : "off";
            args.Add("--idle-timeout");
            args.Add(value);
        }

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName        = exe,
                UseShellExecute = false,
                CreateNoWindow  = true,
                // Daemon's stderr goes to a log file so it doesn't pollute CLI output
                RedirectStandardError  = false,
                RedirectStandardOutput = false,
            }
        };

        foreach (var arg in args)
            proc.StartInfo.ArgumentList.Add(arg);

        proc.Start();
        // Keep process handle so callers can detect early startup exit.
        return Task.FromResult(proc);
    }

    internal static async Task<(DaemonClient? Client, int? ExitCode)> WaitForDaemonAsync(string solutionPath, TimeSpan timeout, Process? startupProcess = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay    = 200;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            delay = Math.Min(delay * 2, 2000); // exponential backoff up to 2s

            var client = await TryConnectAsync(solutionPath);
            if (client is not null)
                return (client, null);

            if (startupProcess is not null && startupProcess.HasExited)
                return (null, startupProcess.ExitCode);
        }

        if (startupProcess is not null && startupProcess.HasExited)
            return (null, startupProcess.ExitCode);

        return (null, null);
    }

    internal static ErrorInfo BuildStartupProcessExitedError(
        string solutionPath,
        int exitCode,
        TimeSpan timeout)
    {
        if (DaemonStartupCoordinator.TryBuildInvalidStaleSocketTypeError(solutionPath, "start", out var staleSocketError))
            return staleSocketError!;

        return new ErrorInfo(
            "DAEMON_STARTUP_PROCESS_EXITED",
            "Daemon process exited before becoming ready.",
            new
            {
                stage = "start",
                exitCode,
                timeoutMs = (long)timeout.TotalMilliseconds
            });
    }

    // ── Messaging ─────────────────────────────────────────────────────────────

    public async Task<DaemonResponse> SendAsync(string command, object? @params = null)
    {
        var request = new DaemonRequest(
            Id:      Guid.NewGuid().ToString("N"),
            Command: command,
            Params:  @params);

        await _writer.WriteLineAsync(JsonOutput.Serialize(request));
        await _writer.FlushAsync();

        var line = await _reader.ReadLineAsync()
            ?? throw new IOException("Daemon closed the connection unexpectedly.");

        return JsonOutput.Deserialize<DaemonResponse>(line)
            ?? throw new InvalidOperationException("Daemon returned invalid JSON.");
    }

    // ── Socket path ───────────────────────────────────────────────────────────

    public static string GetSocketPath(string solutionPath)
    {
        var full  = Path.GetFullPath(solutionPath);
        var hash  = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..12];

        return Path.Combine(GetRuntimeDirectory(), $"dotnet-aicraft-{hash}.sock");
    }

    internal static string GetRuntimeDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.GetTempPath();

        var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var baseDir = GetSecureRuntimeBaseDirectory(xdgRuntimeDir) ?? Path.GetTempPath();

        var userScopedDir = Path.Combine(baseDir, $"dotnet-aicraft-{Environment.UserName}");
        Directory.CreateDirectory(userScopedDir);

        try
        {
            File.SetUnixFileMode(userScopedDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best effort only; not all filesystems support Unix mode bits.
        }

        return userScopedDir;
    }

    private static string? GetSecureRuntimeBaseDirectory(string? candidate)
    {
        if (OperatingSystem.IsWindows())
            return null;

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        if (!Path.IsPathRooted(candidate))
            return null;

        if (!Directory.Exists(candidate))
            return null;

        try
        {
            var attrs = File.GetAttributes(candidate);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                return null;

            var mode = File.GetUnixFileMode(candidate);
            var groupWritable = mode.HasFlag(UnixFileMode.GroupWrite);
            var otherWritable = mode.HasFlag(UnixFileMode.OtherWrite);
            if (groupWritable || otherWritable)
                return null;

            return candidate;
        }
        catch
        {
            return null;
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _reader.Dispose();
        _socket.Dispose();
    }

    internal static async Task ApplyIdleTimeoutAsync(DaemonClient client, DaemonIdleTimeoutSetting setting)
    {
        var value = setting.Enabled ? setting.Normalized : "off";
        var response = await client.SendAsync("setIdleTimeout", new { value });
        if (response.Error is null)
            return;

        var error = response.Error;
        throw new DaemonClientValidationException(error);
    }
}

public sealed class DaemonClientValidationException : Exception
{
    public ErrorInfo Error { get; }

    public DaemonClientValidationException(ErrorInfo error)
        : base(error.Message)
    {
        Error = error;
    }
}
