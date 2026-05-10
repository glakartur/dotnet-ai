using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Unused;

internal static class Entry
{
    private const string CommandName = "unused";

    internal static async Task ExecuteAsync(
        string solutionPath,
        string kind,
        string? project,
        bool publicOnly,
        bool includeGenerated,
        string? idleTimeout)
    {
        if (!Validation.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
        {
            JsonOutput.WriteError(kindError!.Code, kindError.Message, kindError.Details);
            return;
        }

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return;

        await using (client)
        {
            var res = await client.SendAsync(CommandName, new
            {
                kind = normalizedKind,
                project,
                publicOnly,
                includeGenerated
            });

            if (res.Ok)
            {
                JsonOutput.Write(res.Result);
                return;
            }

            JsonOutput.WriteError(
                res.Error?.Code ?? "UNKNOWN_ERROR",
                res.Error?.Message ?? "Unknown daemon error.",
                res.Error?.Details);
        }
    }
}
