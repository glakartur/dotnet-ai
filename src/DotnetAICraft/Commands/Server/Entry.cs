using DotnetAICraft.Diagnostics;
using DotnetAICraft.Output;

namespace DotnetAICraft.Commands.Server;

internal static class Entry
{
    internal static async Task StartAsync(string solutionPath, string? idleTimeout, OutputFormat format = OutputFormat.Text)
    {
        DebugLog.Write("cli", "server-start explicit");

        if (!Validation.TryParseIdleTimeout(idleTimeout, out var timeout, out var error))
        {
            OutputMapping.WriteError(error, format);
            return;
        }

        var startError = await UseCase.EnsureRunningAsync(solutionPath, timeout);
        if (startError is not null)
            OutputMapping.WriteError(startError, format);
    }

    internal static async Task DaemonAsync(string solutionPath, string? idleTimeout, OutputFormat format = OutputFormat.Text)
    {
        if (!Validation.TryParseIdleTimeout(idleTimeout, out var timeout, out var error))
        {
            OutputMapping.WriteError(error, format);
            return;
        }

        var startError = await UseCase.DaemonAsync(solutionPath, timeout);
        if (startError is not null)
            OutputMapping.WriteError(startError, format);
    }

    internal static async Task StopAsync(string solutionPath, OutputFormat format = OutputFormat.Text)
    {
        var (result, error) = await UseCase.StopAsync(solutionPath);
        if (error is not null)
        {
            OutputMapping.WriteError(error, format);
            return;
        }

        OutputMapping.Write(result, format);
    }

    internal static async Task StatusAsync(string solutionPath, OutputFormat format = OutputFormat.Text)
    {
        var result = await UseCase.StatusAsync(solutionPath);
        OutputMapping.Write(result, format);
    }

    internal static async Task ReloadAsync(string solutionPath, string? idleTimeout, OutputFormat format = OutputFormat.Text)
    {
        var (result, error) = await UseCase.ReloadAsync(solutionPath, idleTimeout);
        if (error is not null)
        {
            OutputMapping.WriteError(error, format);
            return;
        }

        if (result is not null)
            OutputMapping.Write(result, format);
    }
}
