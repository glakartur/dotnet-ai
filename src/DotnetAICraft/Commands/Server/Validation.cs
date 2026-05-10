using DotnetAICraft.Daemon;
using DotnetAICraft.Models;

namespace DotnetAICraft.Commands.Server;

internal static class Validation
{
    internal static bool TryParseIdleTimeout(
        string? raw,
        out DaemonIdleTimeoutSetting? timeout,
        out ErrorInfo? error)
    {
        if (DaemonIdleTimeoutParser.TryParseOptional(raw, out timeout, out error))
            return true;

        timeout = null;
        return false;
    }
}
