using DotnetAICraft.Daemon;
using DotnetAICraft.Models;

namespace DotnetAICraft.Commands.Server;

internal static class UseCase
{
    internal static async Task<ErrorInfo?> StartAsync(string solutionPath, DaemonIdleTimeoutSetting? timeout)
    {
        var decision = await DaemonStartupCoordinator.PrepareServerStartAsync(solutionPath);
        if (decision.Type == DaemonServerStartDecisionType.AttachedExisting)
        {
            return null;
        }

        if (decision.Type == DaemonServerStartDecisionType.Failed)
        {
            return decision.Error ?? new ErrorInfo("DAEMON_STARTUP_FAILED", "Daemon startup failed.");
        }

        await using var server = new DaemonServer(solutionPath, timeout, decision.StartupLock);
        await server.RunAsync();
        return null;
    }

    internal static async Task<(object? result, ErrorInfo? error)> StopAsync(string solutionPath)
    {
        var client = await DaemonClient.TryConnectAsync(solutionPath);
        if (client is null)
        {
            return (null, new ErrorInfo("DAEMON_NOT_RUNNING", "No daemon running for this solution."));
        }

        await using (client)
        {
            DaemonResponse res;
            try
            {
                res = await client.SendAsync("shutdown");
            }
            catch (DaemonClientValidationException ex)
            {
                return (null, ex.Error);
            }

            return res.Status == DaemonResponseStatus.Ok
                ? (res.Result, null)
                : (null, res.Error);
        }
    }

    internal static async Task<object> StatusAsync(string solutionPath)
    {
        var client = await DaemonClient.TryConnectAsync(solutionPath);
        if (client is null)
            return new { running = false, solutionPath };

        await using (client)
        {
            DaemonResponse res;
            try
            {
                res = await client.SendAsync("status");
            }
            catch (DaemonClientValidationException ex)
            {
                return new { error = ex.Error };
            }

            return res.Status == DaemonResponseStatus.Ok
                ? res.Result!
                : new { error = res.Error };
        }
    }

    internal static async Task<(object? result, ErrorInfo? error)> ReloadAsync(string solutionPath, string? idleTimeout)
    {
        var client = await DotnetAICraft.Commands.Shared.CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout);
        if (client is null)
            return (null, null);

        await using (client)
        {
            DaemonResponse res;
            try
            {
                if (!DotnetAICraft.Commands.Shared.CommandHelpers.TryParseIdleTimeoutMinutes(idleTimeout, out var idleTimeoutMinutes, out var parseError))
                    return (null, parseError);

                res = await client.SendAsync("reload", idleTimeoutMinutes: idleTimeoutMinutes);
            }
            catch (DaemonClientValidationException ex)
            {
                return (null, ex.Error);
            }

            return res.Status == DaemonResponseStatus.Ok
                ? (res.Result, null)
                : (null, res.Error);
        }
    }
}
