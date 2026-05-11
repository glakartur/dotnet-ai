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
        => response.Data;

    public static async Task<DaemonResponse?> SendOrWriteValidationErrorAsync(
        DaemonClient client,
        string command,
        object? @params = null)
        => await SendOrWriteValidationErrorAsync(() => client.SendAsync(command, @params));

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
        if (response.Error is null)
            return false;

        JsonOutput.WriteError(
            response.Error.Code,
            response.Error.Message,
            response.Error.Details);
        return true;
    }
}
