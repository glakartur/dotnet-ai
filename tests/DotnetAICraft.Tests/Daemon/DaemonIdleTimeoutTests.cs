using DotnetAICraft.Daemon;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DaemonIdleTimeoutParserTests
{
    [Theory]
    [InlineData("off", false, null)]
    [InlineData("OFF", false, null)]
    [InlineData(" off ", false, null)]
    [InlineData("5m", true, 300000)]
    [InlineData("1h", true, 3600000)]
    public void TryParse_ValidValues(string raw, bool enabled, int? expectedMilliseconds)
    {
        var ok = DaemonIdleTimeoutParser.TryParse(raw, out var setting, out var error);

        Assert.True(ok);
        Assert.NotNull(setting);
        Assert.Null(error);
        Assert.Equal(enabled, setting.Enabled);

        if (expectedMilliseconds is not null)
            Assert.Equal(expectedMilliseconds.Value, (int)setting.Duration.TotalMilliseconds);
        else
            Assert.Equal(Timeout.InfiniteTimeSpan, setting.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0m")]
    [InlineData("30s")]
    [InlineData("30S")]
    [InlineData("500ms")]
    [InlineData("500MS")]
    [InlineData(" 30s ")]
    [InlineData("-1m")]
    [InlineData("10")]
    [InlineData("1d")]
    [InlineData("abc")]
    [InlineData("999999999999999999999h")]
    public void TryParse_InvalidValues_ReturnValidationError(string raw)
    {
        var ok = DaemonIdleTimeoutParser.TryParse(raw, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal("INVALID_IDLE_TIMEOUT", error.Code);
    }
}
