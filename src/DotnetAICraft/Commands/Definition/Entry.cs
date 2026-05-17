using System.Text.Json;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Definition;

internal static class Entry
{
    private const string CommandName = "definition";

    internal static async Task ExecuteAsync(
        string solutionPath,
        FileInfo? file,
        int? line,
        int? col,
        string? symbol,
        string? idleTimeout,
        OutputFormat format = OutputFormat.Text)
    {
        Validation.ValidateCliArgs(file, line, col, symbol);

        var @params = !string.IsNullOrWhiteSpace(symbol)
            ? (object)new { symbol = symbol.Trim() }
            : new { file = file!.FullName, line = line!.Value, col = col!.Value };

        var client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout, format);
        if (client is null)
            return;

        await using (client)
        {
            var res = await CommandHelpers.SendOrWriteValidationErrorAsync(client, CommandName, @params, idleTimeout, format: format);
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
                var def = JsonOutput.Deserialize<DefinitionResult>((JsonElement)res.Result!);
                if (def is not null)
                    TextOutput.WriteDefinition(def, solutionPath);
            }
        }
    }
}
