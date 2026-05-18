using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Daemon;

public sealed class DaemonClient : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(120);

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
        DebugLog.Write("client", $"TryConnectAsync socketPath={socketPath}");

        if (!File.Exists(socketPath)) return null;

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            DebugLog.Write("client", "TryConnectAsync connected to existing daemon");
            return new DaemonClient(socket);
        }
        catch
        {
            DebugLog.Write("client", "TryConnectAsync failed to connect");
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
        DebugLog.Write("client", $"ConnectOrStartAsync begin timeoutMs={(long)timeout.TotalMilliseconds}");
        var outcome = await DaemonStartupCoordinator.ConnectOrStartAsync(
            solutionPath,
            parsedTimeout,
            readyTimeout: timeout);

        DebugLog.Write("client", $"ConnectOrStartAsync outcome={outcome.Type} stage={outcome.Stage}");

        if (outcome.Type == DaemonStartupOutcomeType.Failed)
            throw new DaemonClientValidationException(outcome.Error ?? new ErrorInfo("DAEMON_STARTUP_FAILED", "Daemon startup failed."));

        if (outcome.Type == DaemonStartupOutcomeType.StartedNew && DebugLog.IsEnabled)
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

        var args = new List<string> { "server", "daemon", "--solution", solutionPath };
        if (idleTimeout is not null)
        {
            var value = idleTimeout.Enabled ? idleTimeout.Normalized : "off";
            args.Add("--idle-timeout");
            args.Add(value);
        }

        var startInfo = CreateDaemonStartInfo(exe, args);
        ApplySpawnedDaemonEnvironment(startInfo);
        var proc = new Process { StartInfo = startInfo };

        using (StdHandleInheritance.Suppress())
        {
            proc.Start();
        }

        proc.StandardInput.Close();
        _ = DrainProcessPipeAsync(proc.StandardOutput);
        _ = DrainProcessPipeAsync(proc.StandardError);

        return Task.FromResult(proc);
    }

    internal static void ApplySpawnedDaemonEnvironment(ProcessStartInfo startInfo)
    {
        // Per-request debug transport replaces DOTNET_AICRAFT_DEBUG propagation to spawned daemons.
        startInfo.EnvironmentVariables.Remove("DOTNET_AICRAFT_DEBUG");
    }

    internal static ProcessStartInfo CreateDaemonStartInfo(string executablePath, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    internal static async Task DrainProcessPipeAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static async Task<(DaemonClient? Client, int? ExitCode)> WaitForDaemonAsync(string solutionPath, TimeSpan timeout, Process? startupProcess = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay    = 200;
        var attempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            attempts++;
            DebugLog.Write("client", $"WaitForDaemonAsync attempt={attempts} delayMs={delay}");
            delay = Math.Min(delay * 2, 2000); // exponential backoff up to 2s

            var client = await TryConnectAsync(solutionPath);
            if (client is not null)
            {
                DebugLog.Write("client", $"WaitForDaemonAsync connected attempt={attempts}");
                return (client, null);
            }

            if (startupProcess is not null && startupProcess.HasExited)
            {
                DebugLog.Write("client", $"WaitForDaemonAsync startup process exited code={startupProcess.ExitCode}");
                return (null, startupProcess.ExitCode);
            }
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

    public async Task<DaemonResponse> SendAsync(
        string command,
        object? @params = null,
        bool? debug = null,
        int? idleTimeoutMinutes = null,
        PageRequest? page = null)
    {
        DebugLog.Write("client", $"SendAsync begin command={command}");
        var requestDebug = debug ?? (DebugLog.IsEnabled ? true : null);
        var request = new DaemonRequest(
            Id:      Guid.NewGuid().ToString("N"),
            Command: command,
            Params:  @params,
            Debug: requestDebug,
            IdleTimeoutMinutes: idleTimeoutMinutes,
            Page: page);

        try
        {
            await _writer.WriteLineAsync(JsonOutput.Serialize(request));
            await _writer.FlushAsync();
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            DebugLog.Write("client", $"SendAsync transport failure on write command={command} type={ex.GetType().FullName}");
            throw new DaemonTransportException(BuildTransportError(command, "write", ex), ex);
        }

        DebugLog.Write("client", $"SendAsync request sent command={command} requestId={request.Id}");

        string line;
        try
        {
            line = await ReadResponseLineOrThrowAsync(_reader, command, DefaultResponseTimeout);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            DebugLog.Write("client", $"SendAsync transport failure on read command={command} type={ex.GetType().FullName}");
            throw new DaemonTransportException(BuildTransportError(command, "read", ex), ex);
        }

        DebugLog.Write("client", $"SendAsync response line received command={command} length={line.Length}");
        return ParseResponseOrThrow(line, command);
    }

    private static bool IsTransportFailure(Exception ex)
        => ex is IOException or SocketException or ObjectDisposedException;

    private static ErrorInfo BuildTransportError(string command, string stage, Exception inner)
        => new(
            "DAEMON_TRANSPORT_FAILED",
            "Daemon transport failed.",
            new
            {
                command,
                stage,
                innerType = inner.GetType().FullName,
                innerMessage = inner.Message
            });

    internal static async Task<string> ReadResponseLineOrThrowAsync(
        TextReader reader,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readTask = reader.ReadLineAsync(timeoutCts.Token).AsTask();
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        DebugLog.Write("client", $"ReadResponseLineOrThrowAsync wait command={command} timeoutMs={(long)timeout.TotalMilliseconds}");

        var completed = await Task.WhenAny(readTask, timeoutTask);
        if (completed == timeoutTask)
        {
            DebugLog.Write("client", $"ReadResponseLineOrThrowAsync timeout command={command}");
            timeoutCts.Cancel();
            throw new DaemonClientValidationException(
                new ErrorInfo(
                    "DAEMON_RESPONSE_TIMEOUT",
                    "Timed out waiting for daemon response.",
                    new
                    {
                        command,
                        timeoutMs = (long)timeout.TotalMilliseconds
                    }));
        }

        string? line;
        try
        {
            line = await readTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DebugLog.Write("client", $"ReadResponseLineOrThrowAsync canceled by timeout command={command}");
            throw new DaemonClientValidationException(
                new ErrorInfo(
                    "DAEMON_RESPONSE_TIMEOUT",
                    "Timed out waiting for daemon response.",
                    new
                    {
                        command,
                        timeoutMs = (long)timeout.TotalMilliseconds
                    }));
        }

        if (line is null)
        {
            DebugLog.Write("client", $"ReadResponseLineOrThrowAsync incomplete response command={command}");
            throw new DaemonClientValidationException(
                new ErrorInfo(
                    "DAEMON_RESPONSE_INCOMPLETE",
                    "Daemon closed the connection before sending a complete response.",
                    new { command }));
        }

        return line;
    }

    internal static DaemonResponse ParseResponseOrThrow(string line, string command)
    {
        JsonDocument responseDoc;
        try
        {
            responseDoc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            DebugLog.Write("client", $"ParseResponseOrThrow invalid JSON command={command}");
            throw new DaemonClientValidationException(
                new ErrorInfo(
                    "DAEMON_RESPONSE_INVALID_JSON",
                    "Daemon returned invalid JSON response.",
                    new { command }));
        }

        using (responseDoc)
        {
            if (responseDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DaemonClientValidationException(
                    new ErrorInfo(
                        "DAEMON_RESPONSE_INVALID_JSON",
                        "Daemon returned invalid JSON response.",
                        new { command }));
            }

            var root = responseDoc.RootElement;
            var hasStatus = root.TryGetProperty("status", out var statusElement);
            var hasLegacyData = root.TryGetProperty("data", out _);

            if (!hasStatus)
            {
                var mismatchCode = hasLegacyData ? "DAEMON_PROTOCOL_MISMATCH" : "DAEMON_RESPONSE_INVALID_STATUS";
                var mismatchMessage = hasLegacyData
                    ? "Daemon response uses legacy envelope. Client/server protocol versions are incompatible."
                    : "Daemon response is missing required status field.";

                throw new DaemonClientValidationException(
                    new ErrorInfo(
                        mismatchCode,
                        mismatchMessage,
                        new { command }));
            }

            if (statusElement.ValueKind == JsonValueKind.Null)
            {
                throw new DaemonClientValidationException(
                    new ErrorInfo(
                        "DAEMON_RESPONSE_INVALID_STATUS",
                        "Daemon response status cannot be null.",
                        new { command }));
            }

            if (statusElement.ValueKind != JsonValueKind.String)
            {
                throw new DaemonClientValidationException(
                    new ErrorInfo(
                        "DAEMON_RESPONSE_INVALID_STATUS",
                        "Daemon response status must be a string value.",
                        new { command }));
            }

            var rawStatus = statusElement.GetString();
            if (rawStatus is not ("ok" or "problem" or "error"))
            {
                throw new DaemonClientValidationException(
                    new ErrorInfo(
                        "DAEMON_RESPONSE_INVALID_STATUS",
                        "Daemon returned unsupported status value.",
                        new { command, status = rawStatus }));
            }

            DaemonResponse? response;
            try
            {
                response = JsonOutput.Deserialize<DaemonResponse>(responseDoc.RootElement);
            }
            catch (JsonException)
            {
                response = null;
            }

            if (response is null)
            {
                DebugLog.Write("client", $"ParseResponseOrThrow invalid JSON command={command}");
                throw new DaemonClientValidationException(
                    new ErrorInfo(
                        "DAEMON_RESPONSE_INVALID_JSON",
                        "Daemon returned invalid JSON response.",
                        new { command }));
            }

            var validationError = response.ValidateContract(command);
            if (validationError is not null)
                throw new DaemonClientValidationException(validationError);

            DebugLog.Write("client", $"ParseResponseOrThrow parsed command={command} status={response.Status.ToString().ToLowerInvariant()} hasError={response.Error is not null}");
            return response;
        }
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
        if (response.Status == DaemonResponseStatus.Ok)
            return;

        var error = response.Error;
        if (error is null)
        {
            throw new DaemonClientValidationException(
                new ErrorInfo(
                    "DAEMON_RESPONSE_CONTRACT_VIOLATION",
                    "Daemon returned non-ok status without error payload.",
                    new { status = response.Status.ToString().ToLowerInvariant() }));
        }

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

public sealed class DaemonTransportException : Exception
{
    public ErrorInfo Error { get; }

    public DaemonTransportException(ErrorInfo error)
        : base(error.Message)
    {
        Error = error;
    }

    public DaemonTransportException(ErrorInfo error, Exception innerException)
        : base(error.Message, innerException)
    {
        Error = error;
    }
}
