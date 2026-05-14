using System.IO;
using System.Threading;
using DotnetAICraft.Daemon;
using DotnetAICraft.Models;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DaemonResponseCompletionTests
{
    [Fact]
    public async Task ReadResponseLineOrThrowAsync_WhenResponseNeverArrives_ThrowsTimeoutValidationError()
    {
        var reader = new BlockingTextReader();

        var ex = await Assert.ThrowsAsync<DaemonClientValidationException>(() =>
            DaemonClient.ReadResponseLineOrThrowAsync(
                reader,
                command: "symbols",
                timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal("DAEMON_RESPONSE_TIMEOUT", ex.Error.Code);
    }

    [Fact]
    public async Task ReadResponseLineOrThrowAsync_WhenConnectionCloses_ThrowsIncompleteValidationError()
    {
        using var stringReader = new StringReader(string.Empty);

        var ex = await Assert.ThrowsAsync<DaemonClientValidationException>(() =>
            DaemonClient.ReadResponseLineOrThrowAsync(
                stringReader,
                command: "symbols",
                timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal("DAEMON_RESPONSE_INCOMPLETE", ex.Error.Code);
    }

    [Fact]
    public void ParseResponseOrThrow_WhenJsonInvalid_ThrowsInvalidJsonValidationError()
    {
        var ex = Assert.Throws<DaemonClientValidationException>(() =>
            DaemonClient.ParseResponseOrThrow("not-json", "symbols"));

        Assert.Equal("DAEMON_RESPONSE_INVALID_JSON", ex.Error.Code);
    }

    [Fact]
    public void ParseResponseOrThrow_WhenLegacyEnvelopeReturned_ThrowsProtocolMismatch()
    {
        const string legacyJson = "{\"id\":\"req\",\"data\":[],\"error\":null}";

        var ex = Assert.Throws<DaemonClientValidationException>(() =>
            DaemonClient.ParseResponseOrThrow(legacyJson, "symbols"));

        Assert.Equal("DAEMON_PROTOCOL_MISMATCH", ex.Error.Code);
    }

    [Fact]
    public void ParseResponseOrThrow_WhenStatusNull_ThrowsInvalidStatus()
    {
        const string nullStatusJson = "{\"id\":\"req\",\"status\":null,\"error\":{\"code\":\"X\",\"message\":\"m\"}}";

        var ex = Assert.Throws<DaemonClientValidationException>(() =>
            DaemonClient.ParseResponseOrThrow(nullStatusJson, "symbols"));

        Assert.Equal("DAEMON_RESPONSE_INVALID_STATUS", ex.Error.Code);
    }

    [Fact]
    public void ParseResponseOrThrow_WhenStatusUnknown_ThrowsInvalidStatus()
    {
        const string invalidStatusJson = "{\"id\":\"req\",\"status\":\"unknown\",\"error\":{\"code\":\"X\",\"message\":\"m\"}}";

        var ex = Assert.Throws<DaemonClientValidationException>(() =>
            DaemonClient.ParseResponseOrThrow(invalidStatusJson, "symbols"));

        Assert.Equal("DAEMON_RESPONSE_INVALID_STATUS", ex.Error.Code);
    }

    [Fact]
    public void ParseResponseOrThrow_WhenOkWithError_ThrowsContractViolation()
    {
        var invalid = new DaemonResponse(
            Id: "req",
            Status: DaemonResponseStatus.Ok,
            Result: new[] { "x" },
            Error: new ErrorInfo("BAD", "Should be null for ok."),
            Debug: null,
            Page: null,
            Meta: null);
        var json = DotnetAICraft.Output.JsonOutput.Serialize(invalid);

        var ex = Assert.Throws<DaemonClientValidationException>(() =>
            DaemonClient.ParseResponseOrThrow(json, "symbols"));

        Assert.Equal("DAEMON_RESPONSE_CONTRACT_VIOLATION", ex.Error.Code);
    }

    [Fact]
    public void ParseResponseOrThrow_WhenProblemWithoutError_ThrowsContractViolation()
    {
        var invalid = new DaemonResponse(
            Id: "req",
            Status: DaemonResponseStatus.Problem,
            Result: null,
            Error: null,
            Debug: null,
            Page: null,
            Meta: null);
        var json = DotnetAICraft.Output.JsonOutput.Serialize(invalid);

        var ex = Assert.Throws<DaemonClientValidationException>(() =>
            DaemonClient.ParseResponseOrThrow(json, "symbols"));

        Assert.Equal("DAEMON_RESPONSE_CONTRACT_VIOLATION", ex.Error.Code);
    }

    private sealed class BlockingTextReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}
