using System.IO;
using System.Threading;
using DotnetAICraft.Daemon;
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

    private sealed class BlockingTextReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}
