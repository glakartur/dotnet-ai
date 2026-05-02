using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DotnetAi.Models;
using DotnetAi.Output;

namespace DotnetAi.Daemon;

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

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!File.Exists(socketPath)) return null;
        }

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

        var client = await TryConnectAsync(solutionPath);
        if (client is not null)
        {
            if (parsedTimeout is not null)
            {
                try
                {
                    await ApplyIdleTimeoutAsync(client, parsedTimeout);
                }
                catch
                {
                    await client.DisposeAsync();
                    throw;
                }
            }

            return client;
        }

        Console.Error.WriteLine("[dotnet-ai] Starting analysis daemon (first run loads the solution)...");
        await StartDaemonProcessAsync(solutionPath, parsedTimeout);

        var timeout = startTimeout ?? TimeSpan.FromSeconds(120);
        client = await WaitForDaemonAsync(solutionPath, timeout)
            ?? throw new TimeoutException(
                $"Daemon did not start within {timeout.TotalSeconds}s. " +
                $"Check stderr above for errors.");

        Console.Error.WriteLine("[dotnet-ai] Ready.");
        return client;
    }

    private static async Task StartDaemonProcessAsync(string solutionPath, DaemonIdleTimeoutSetting? idleTimeout)
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
        // Detach — daemon outlives this CLI process
    }

    private static async Task<DaemonClient?> WaitForDaemonAsync(string solutionPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay    = 200;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            delay = Math.Min(delay * 2, 2000); // exponential backoff up to 2s

            var client = await TryConnectAsync(solutionPath);
            if (client is not null) return client;
        }
        return null;
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

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $@"\\.\pipe\dotnet-ai-{hash}"
            : Path.Combine(Path.GetTempPath(), $"dotnet-ai-{hash}.sock");
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _reader.Dispose();
        _socket.Dispose();
    }

    private static async Task ApplyIdleTimeoutAsync(DaemonClient client, DaemonIdleTimeoutSetting setting)
    {
        var value = setting.Enabled ? setting.Normalized : "off";
        var response = await client.SendAsync("setIdleTimeout", new { value });
        if (response.Ok)
            return;

        var error = response.Error ?? new ErrorInfo("INVALID_IDLE_TIMEOUT", "Invalid idle timeout value.");
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
