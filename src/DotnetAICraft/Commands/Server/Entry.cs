namespace DotnetAICraft.Commands.Server;

internal static class Entry
{
    internal static async Task StartAsync(string solutionPath, string? idleTimeout)
    {
        if (!Validation.TryParseIdleTimeout(idleTimeout, out var timeout, out var error))
        {
            OutputMapping.WriteError(error);
            return;
        }

        var startError = await UseCase.StartAsync(solutionPath, timeout);
        if (startError is not null)
            OutputMapping.WriteError(startError);
    }

    internal static async Task StopAsync(string solutionPath)
    {
        var (result, error) = await UseCase.StopAsync(solutionPath);
        if (error is not null)
        {
            OutputMapping.WriteError(error);
            return;
        }

        OutputMapping.Write(result);
    }

    internal static async Task StatusAsync(string solutionPath)
    {
        var result = await UseCase.StatusAsync(solutionPath);
        OutputMapping.Write(result);
    }

    internal static async Task ReloadAsync(string solutionPath, string? idleTimeout)
    {
        var (result, error) = await UseCase.ReloadAsync(solutionPath, idleTimeout);
        if (error is not null)
        {
            OutputMapping.WriteError(error);
            return;
        }

        if (result is not null)
            OutputMapping.Write(result);
    }
}
