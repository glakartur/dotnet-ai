using DotnetAICraft.Daemon;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Shared;

internal static class CommandHelpers
{
    public static async Task<DaemonClient?> ConnectOrWriteValidationErrorAsync(
        string solutionPath,
        string? idleTimeout)
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
            JsonOutput.WriteError(ex.Error.Code, ex.Error.Message, ex.Error.Details);
            return null;
        }
    }

    public static object? GetDataOrNull(DaemonResponse response)
        => response.Result;

    public static async Task<DaemonResponse?> SendOrWriteValidationErrorAsync(
        DaemonClient client,
        string command,
        object? @params = null,
        string? idleTimeout = null,
        PageRequest? page = null)
    {
        if (!TryParseIdleTimeoutMinutes(idleTimeout, out var idleTimeoutMinutes, out var parseError))
        {
            JsonOutput.WriteError(parseError!.Code, parseError.Message, parseError.Details);
            return null;
        }

        return await SendOrWriteValidationErrorAsync(() => client.SendAsync(command, @params, idleTimeoutMinutes: idleTimeoutMinutes, page: page));
    }

    internal static async Task<DaemonResponse?> SendOrWriteValidationErrorAsync(
        Func<Task<DaemonResponse>> send)
    {
        DebugLog.Write("client", "SendOrWriteValidationErrorAsync begin");
        try
        {
            var response = await send();
            DebugLog.Write("client", "SendOrWriteValidationErrorAsync response received");
            return response;
        }
        catch (DaemonClientValidationException ex)
        {
            DebugLog.Write("client", $"SendOrWriteValidationErrorAsync validation error code={ex.Error.Code}");
            JsonOutput.WriteError(ex.Error.Code, ex.Error.Message, ex.Error.Details);
            return null;
        }
    }

    public static bool TryHandleError(DaemonResponse response)
    {
        if (response.Status == DaemonResponseStatus.Ok)
            return false;

        if (response.Error is null)
        {
            JsonOutput.WriteError(
                "DAEMON_RESPONSE_CONTRACT_VIOLATION",
                "Daemon returned non-ok status without error payload.",
                new { status = response.Status.ToString().ToLowerInvariant() });
            return true;
        }

        JsonOutput.WriteError(
            response.Error.Code,
            response.Error.Message,
            response.Error.Details);
        return true;
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
