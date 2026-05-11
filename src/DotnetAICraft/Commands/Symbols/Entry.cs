using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Diagnostics;
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
        string? idleTimeout)
    {
        DebugLog.Write("symbols", $"ExecuteAsync begin solution={solutionPath} pattern={pattern} kind={kind} limit={limit} offset={offset}");

        if (!Validation.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
        {
            JsonOutput.WriteError(kindError!.Code, kindError.Message, kindError.Details);
            return;
        }

        if (!Validation.TryNormalizePagination(limit, offset, out var normalizedLimit, out var normalizedOffset, out var paginationError))
        {
            JsonOutput.WriteError(paginationError!.Code, paginationError.Message, paginationError.Details);
            return;
        }

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, new
            {
                pattern,
                kind = normalizedKind,
                limit = normalizedLimit,
                offset = normalizedOffset
            });

            if (res is null)
                return;

            if (CommandHelpers.TryHandleError(res))
                return;

            DebugLog.Write("symbols", "ExecuteAsync writing output to stdout");
            JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
            DebugLog.Write("symbols", "ExecuteAsync finished");
        }
    }
}
