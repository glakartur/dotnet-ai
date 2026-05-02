using DotnetAi.Daemon;
using DotnetAi.Output;

namespace DotnetAi.Commands;

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
}
