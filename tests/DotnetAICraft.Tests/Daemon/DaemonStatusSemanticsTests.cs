using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Models;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

[Collection("Console output")]
public class DaemonStatusSemanticsTests
{
    [Theory]
    [InlineData(DaemonResponseStatus.Problem)]
    [InlineData(DaemonResponseStatus.Error)]
    public void TryHandleError_NonOkWithoutError_ReturnsContractViolation(DaemonResponseStatus status)
    {
        var response = new DaemonResponse(
            Id: "req",
            Status: status,
            Result: null,
            Error: null,
            Debug: null,
            Page: null,
            Meta: null);

        using var capture = ConsoleOutputCapture.Start();
        var handled = CommandHelpers.TryHandleError(response);
        Assert.True(handled);

        var output = capture.GetOutput();
        Assert.Contains("DAEMON_RESPONSE_CONTRACT_VIOLATION", output, StringComparison.Ordinal);
    }
}
