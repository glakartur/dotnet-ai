using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Shared;

internal static class CommandHelpers
{
    public static async Task<DaemonClient?> ConnectOrWriteValidationErrorAsync(
        string solutionPath,
        string? idleTimeout)
    {
        try
        {
            return await DaemonClient.ConnectOrStartAsync(solutionPath, idleTimeout: idleTimeout);
        }
        catch (DaemonClientValidationException ex)
        {
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
        try
        {
            return await send();
        }
        catch (DaemonClientValidationException ex)
        {
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
