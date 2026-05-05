using DotnetAICraft.Daemon;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class DaemonTimeoutValidationTests
{
    [Fact]
    public void TryParseOptional_Null_DoesNotMutateState()
    {
        var ok = DaemonIdleTimeoutParser.TryParseOptional(null, out var setting, out var error);

        Assert.True(ok);
        Assert.Null(setting);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("-1s")]
    [InlineData("weird")]
    [InlineData(" ")]
    public void TryParseOptional_Invalid_ReturnsErrorWithoutSetting(string raw)
    {
        var ok = DaemonIdleTimeoutParser.TryParseOptional(raw, out var setting, out var error);

        Assert.False(ok);
        Assert.Null(setting);
        Assert.NotNull(error);
        Assert.Equal("INVALID_IDLE_TIMEOUT", error.Code);
    }
}
