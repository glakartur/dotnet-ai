using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Impls;

internal static class Entry
{
    private const string CommandName = "impls";

    internal static async Task ExecuteAsync(
        string solutionPath,
        string symbol,
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout, format);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, new { symbol }, idleTimeout, format: format);
            if (res is null)
                return;

            if (CommandHelpers.TryHandleError(res, format))
                return;

            if (format == OutputFormat.Json)
            {
                JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
            }
            else
            {
                var items = JsonOutput.Deserialize<IReadOnlyList<SymbolResult>>((JsonElement)res.Result!) ?? Array.Empty<SymbolResult>();
                TextOutput.WriteImpls(items, symbol, solutionPath);
            }
        }
    }
}
