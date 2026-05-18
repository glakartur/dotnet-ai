using DotnetAICraft.Daemon;
using DotnetAICraft.Models;

namespace DotnetAICraft.Diagnostics;

internal static class TopLevelExceptionFirewall
{
    public static ErrorInfo Map(Exception exception)
        => exception switch
        {
            DaemonTransportException transport       => transport.Error,
            DaemonClientValidationException validation => validation.Error,
            _ => new ErrorInfo(
                "INTERNAL_ERROR",
                exception.Message,
                new { type = exception.GetType().FullName })
        };
}
