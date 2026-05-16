using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Symbols;

internal static class Entry
{
    private const string CommandName = "symbols";

    internal static async Task ExecuteAsync(
        string solutionPath,
        string pattern,
        string kind,
        int limit,
        int offset,
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        DebugLog.Write("symbols", $"ExecuteAsync begin solution={solutionPath} pattern={pattern} kind={kind} limit={limit} offset={offset}");

        if (!Validation.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
        {
            CommandHelpers.WriteError(format, kindError!.Code, kindError.Message, kindError.Details);
            return;
        }

        if (!Validation.TryNormalizePagination(limit, offset, out var normalizedLimit, out var normalizedOffset, out var paginationError))
        {
            CommandHelpers.WriteError(format, paginationError!.Code, paginationError.Message, paginationError.Details);
            return;
        }

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout, format);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(
                client,
                CommandName,
                new
                {
                    pattern,
                    kind = normalizedKind
                },
                idleTimeout,
                page: new DotnetAICraft.Models.PageRequest(normalizedOffset, normalizedLimit),
                format: format);

            if (res is null)
                return;

            if (CommandHelpers.TryHandleError(res, format))
                return;

            DebugLog.Write("symbols", "ExecuteAsync writing output to stdout");
            if (format == OutputFormat.Json)
            {
                JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
            }
            else
            {
                var items = JsonOutput.Deserialize<IReadOnlyList<SymbolResult>>((JsonElement)res.Result!)
                    ?? Array.Empty<SymbolResult>();
                var hasMore = res.Page?.HasMore ?? false;
                TextOutput.WriteSymbols(new SymbolsResultPage(items, hasMore), pattern, solutionPath);
            }
            DebugLog.Write("symbols", "ExecuteAsync finished");
        }
    }
}
