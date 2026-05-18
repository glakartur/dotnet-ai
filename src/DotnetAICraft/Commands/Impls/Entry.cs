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
        var res = await CommandHelpers.SendWithRetryOrWriteErrorAsync(
            solutionPath, CommandName, new { symbol }, idleTimeout, format: format);
        if (res is null)
            return;

        if (CommandHelpers.TryHandleError(res, format))
            return;

        var solutionDir = Path.GetDirectoryName(solutionPath) ?? string.Empty;
        if (format == OutputFormat.Json)
        {
            JsonOutput.WriteWithSolutionRoot(solutionDir, CommandHelpers.GetDataOrNull(res));
        }
        else
        {
            TextOutput.WriteSolutionRootHeader(solutionDir);
            var items = JsonOutput.Deserialize<IReadOnlyList<SymbolResult>>((JsonElement)res.Result!) ?? Array.Empty<SymbolResult>();
            TextOutput.WriteImpls(items, symbol, solutionPath);
        }
    }
}
