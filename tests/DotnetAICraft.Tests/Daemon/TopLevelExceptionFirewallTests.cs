using DotnetAICraft.Daemon;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public sealed class TopLevelExceptionFirewallTests
{
    [Fact]
    public void Map_DaemonTransportException_PreservesItsErrorInfo()
    {
        var inner = new IOException("pipe broken");
        var original = new ErrorInfo(
            "DAEMON_TRANSPORT_FAILED",
            "Daemon transport failed.",
            new { command = "symbols", stage = "write" });

        var error = TopLevelExceptionFirewall.Map(new DaemonTransportException(original, inner));

        Assert.Same(original, error);
    }

    [Fact]
    public void Map_DaemonClientValidationException_PreservesItsErrorInfo()
    {
        var original = new ErrorInfo("INVALID_IDLE_TIMEOUT", "bad", new { acceptedValues = "off | <duration>" });

        var error = TopLevelExceptionFirewall.Map(new DaemonClientValidationException(original));

        Assert.Same(original, error);
    }

    [Fact]
    public void Map_UnknownException_ReturnsInternalErrorWithTypeAndMessage()
    {
        var error = TopLevelExceptionFirewall.Map(new InvalidOperationException("boom"));

        Assert.Equal("INTERNAL_ERROR", error.Code);
        Assert.Equal("boom", error.Message);

        var details = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(error.Details));
        Assert.Equal(typeof(InvalidOperationException).FullName, details.RootElement.GetProperty("type").GetString());
    }
}
