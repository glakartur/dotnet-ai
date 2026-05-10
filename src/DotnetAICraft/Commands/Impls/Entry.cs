using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Impls;

internal static class Entry
{
    private const string CommandName = "impls";

    internal static async Task ExecuteAsync(
        string solutionPath,
        string symbol,
        string? idleTimeout)
    {
        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return;

        await using (client)
        {
            var res = await client.SendAsync(CommandName, new { symbol });
            if (CommandHelpers.TryHandleError(res))
                return;

            JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
        }
    }
}
