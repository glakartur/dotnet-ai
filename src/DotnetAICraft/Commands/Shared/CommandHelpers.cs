using DotnetAICraft.Daemon;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Shared;

internal static class CommandHelpers
{
    public static async Task<DaemonClient?> ConnectOrWriteValidationErrorAsync(
        string solutionPath,
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        DebugLog.Write("client", $"ConnectOrWriteValidationErrorAsync begin solution={solutionPath} idleTimeout={idleTimeout ?? "<null>"}");
        try
        {
            var client = await DaemonClient.ConnectOrStartAsync(solutionPath, idleTimeout: idleTimeout);
            DebugLog.Write("client", "ConnectOrWriteValidationErrorAsync connected");
            return client;
        }
        catch (DaemonClientValidationException ex)
        {
            DebugLog.Write("client", $"ConnectOrWriteValidationErrorAsync validation error code={ex.Error.Code}");
            WriteError(format, ex.Error.Code, ex.Error.Message, ex.Error.Details);
            return null;
        }
    }

    public static object? GetDataOrNull(DaemonResponse response)
        => response.Result;

    public static async Task<DaemonResponse?> SendWithRetryOrWriteErrorAsync(
        string solutionPath,
        string command,
        object? @params = null,
        string? idleTimeout = null,
        PageRequest? page = null,
        OutputFormat format = OutputFormat.Text)
    {
        if (!TryParseIdleTimeoutMinutes(idleTimeout, out var idleTimeoutMinutes, out var parseError))
        {
            WriteError(format, parseError!.Code, parseError.Message, parseError.Details);
            return null;
        }

        var result = await SendWithRetryCoreAsync(
            connect: () => DaemonClient.ConnectOrStartAsync(solutionPath, idleTimeout: idleTimeout),
            send:    client => client.SendAsync(command, @params, idleTimeoutMinutes: idleTimeoutMinutes, page: page),
            onRestart: () => Console.Error.WriteLine("[dotnet-aicraft] Daemon connection lost. Restarting..."));

        if (result.Error is not null)
        {
            WriteError(format, result.Error.Code, result.Error.Message, result.Error.Details);
            return null;
        }

        if (result.Response is not null)
            FlushResponseDebugToStderr(result.Response);

        return result.Response;
    }

    internal sealed record RetryResult(DaemonResponse? Response, ErrorInfo? Error, int SendAttempts);

    internal static async Task<RetryResult> SendWithRetryCoreAsync(
        Func<Task<DaemonClient>> connect,
        Func<DaemonClient, Task<DaemonResponse>> send,
        Action onRestart)
    {
        DaemonClient? client;
        try
        {
            client = await connect();
        }
        catch (DaemonClientValidationException ex)
        {
            return new RetryResult(null, ex.Error, 0);
        }
        catch (DaemonTransportException ex)
        {
            return new RetryResult(null, ex.Error, 0);
        }

        var attempts = 0;
        try
        {
            try
            {
                attempts++;
                var response = await send(client);
                return new RetryResult(response, null, attempts);
            }
            catch (DaemonClientValidationException ex)
            {
                return new RetryResult(null, ex.Error, attempts);
            }
            catch (DaemonTransportException)
            {
                DebugLog.Write("client", "SendWithRetryCoreAsync transport failure; restarting and retrying once");
                onRestart();
                await client.DisposeAsync();
                client = null;
            }

            try
            {
                client = await connect();
            }
            catch (DaemonClientValidationException ex)
            {
                return new RetryResult(null, ex.Error, attempts);
            }
            catch (DaemonTransportException ex)
            {
                return new RetryResult(null, ex.Error, attempts);
            }

            try
            {
                attempts++;
                var response = await send(client);
                return new RetryResult(response, null, attempts);
            }
            catch (DaemonClientValidationException ex)
            {
                return new RetryResult(null, ex.Error, attempts);
            }
            catch (DaemonTransportException ex)
            {
                return new RetryResult(null, ex.Error, attempts);
            }
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
        }
    }

    public static async Task<DaemonResponse?> SendOrWriteValidationErrorAsync(
        DaemonClient client,
        string command,
        object? @params = null,
        string? idleTimeout = null,
        PageRequest? page = null,
        OutputFormat format = OutputFormat.Text)
    {
        if (!TryParseIdleTimeoutMinutes(idleTimeout, out var idleTimeoutMinutes, out var parseError))
        {
            WriteError(format, parseError!.Code, parseError.Message, parseError.Details);
            return null;
        }

        return await SendOrWriteValidationErrorAsync(() => client.SendAsync(command, @params, idleTimeoutMinutes: idleTimeoutMinutes, page: page), format);
    }

    internal static async Task<DaemonResponse?> SendOrWriteValidationErrorAsync(
        Func<Task<DaemonResponse>> send,
        OutputFormat format = OutputFormat.Text)
    {
        DebugLog.Write("client", "SendOrWriteValidationErrorAsync begin");
        try
        {
            var response = await send();
            DebugLog.Write("client", "SendOrWriteValidationErrorAsync response received");
            FlushResponseDebugToStderr(response);
            return response;
        }
        catch (DaemonClientValidationException ex)
        {
            DebugLog.Write("client", $"SendOrWriteValidationErrorAsync validation error code={ex.Error.Code}");
            WriteError(format, ex.Error.Code, ex.Error.Message, ex.Error.Details);
            return null;
        }
        catch (DaemonTransportException ex)
        {
            DebugLog.Write("client", $"SendOrWriteValidationErrorAsync transport error code={ex.Error.Code}");
            JsonOutput.WriteError(ex.Error.Code, ex.Error.Message, ex.Error.Details);
            return null;
        }
    }

    public static bool TryHandleError(DaemonResponse response, OutputFormat format = OutputFormat.Text)
    {
        if (response.Status == DaemonResponseStatus.Ok)
            return false;

        if (response.Error is null)
        {
            WriteError(
                format,
                "DAEMON_RESPONSE_CONTRACT_VIOLATION",
                "Daemon returned non-ok status without error payload.",
                new { status = response.Status.ToString().ToLowerInvariant() });
            return true;
        }

        WriteError(
            format,
            response.Error.Code,
            response.Error.Message,
            response.Error.Details);
        return true;
    }

    internal static void WriteError(OutputFormat format, string code, string message, object? details)
    {
        if (format == OutputFormat.Json)
            JsonOutput.WriteError(code, message, details);
        else
            TextOutput.WriteError(code, message, details);
    }

    internal static void FlushResponseDebugToStderr(DaemonResponse response)
    {
        if (response.Debug is null) return;

        var lines = ExtractDebugLines(response.Debug);
        if (lines.Length == 0) return;

        DebugLog.WriteResponseDebug(lines);
    }

    private static string[] ExtractDebugLines(object payload)
    {
        if (payload is string[] arr)
            return arr;

        if (payload is IEnumerable<string> seq)
            return seq.ToArray();

        if (payload is System.Text.Json.JsonElement element
            && element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    list.Add(item.GetString() ?? string.Empty);
            }
            return list.ToArray();
        }

        return Array.Empty<string>();
    }

    internal static bool TryParseIdleTimeoutMinutes(string? idleTimeout, out int? idleTimeoutMinutes, out ErrorInfo? error)
    {
        idleTimeoutMinutes = null;

        if (!DaemonIdleTimeoutParser.TryParseOptional(idleTimeout, out var parsedTimeout, out var parseError))
        {
            error = parseError;
            return false;
        }

        if (parsedTimeout is not { Enabled: true })
        {
            error = null;
            return true;
        }

        try
        {
            idleTimeoutMinutes = checked((int)parsedTimeout.Duration.TotalMinutes);
            error = null;
            return true;
        }
        catch (OverflowException)
        {
            error = new ErrorInfo(
                "INVALID_IDLE_TIMEOUT",
                "Idle timeout value is too large.",
                new { acceptedValues = "off | <positive duration with unit: m|h>" });
            return false;
        }
    }
}
