using System.Text.Json;
using DotnetAICraft.Models;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Server;

internal static class OutputMapping
{
    internal static void WriteError(ErrorInfo? error, OutputFormat format = OutputFormat.Text, string fallbackCode = "UNKNOWN_ERROR", string fallbackMessage = "Unknown daemon error.")
    {
        var code = error?.Code ?? fallbackCode;
        var message = error?.Message ?? fallbackMessage;
        var details = error?.Details;

        if (format == OutputFormat.Json)
            JsonOutput.WriteError(code, message, details);
        else
            TextOutput.WriteError(code, message, details);
    }

    internal static void Write(object? value, OutputFormat format = OutputFormat.Text)
    {
        if (format == OutputFormat.Json)
        {
            JsonOutput.Write(value);
            return;
        }

        // For text format, try to interpret common shapes.
        if (value is null)
            return;

        // Try DaemonStatus shape
        if (value is JsonElement el)
        {
            var status = TryDeserialize<DaemonStatus>(el);
            if (status is not null && !string.IsNullOrEmpty(status.SolutionPath))
            {
                TextOutput.WriteServerStatus(status);
                return;
            }
            // Fallback: emit as JSON
            JsonOutput.Write(value);
            return;
        }

        // Anonymous fallback objects (e.g. { running = false, solutionPath }): serialize then re-parse
        try
        {
            var json = JsonOutput.Serialize(value);
            using var doc = JsonDocument.Parse(json);
            var elClone = doc.RootElement.Clone();
            var status = TryDeserialize<DaemonStatus>(elClone);
            if (status is not null && !string.IsNullOrEmpty(status.SolutionPath))
            {
                TextOutput.WriteServerStatus(status);
                return;
            }
            // Fallback: emit raw JSON
            JsonOutput.Write(value);
        }
        catch
        {
            JsonOutput.Write(value);
        }
    }

    private static T? TryDeserialize<T>(JsonElement element)
    {
        try
        {
            return JsonOutput.Deserialize<T>(element);
        }
        catch
        {
            return default;
        }
    }
}
