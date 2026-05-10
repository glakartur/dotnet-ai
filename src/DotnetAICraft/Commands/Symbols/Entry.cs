using DotnetAICraft.Commands.Shared;
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
            var res = await client.SendAsync(CommandName, new
            {
                pattern,
                kind = normalizedKind,
                limit = normalizedLimit,
                offset = normalizedOffset
            });

            if (CommandHelpers.TryHandleError(res))
                return;

            JsonOutput.Write(CommandHelpers.GetDataOrNull(res));
        }
    }
}
