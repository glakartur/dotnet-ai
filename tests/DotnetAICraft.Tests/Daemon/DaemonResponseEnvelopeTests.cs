using DotnetAICraft.Models;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DaemonResponseEnvelopeTests
{
    [Fact]
    public void Serialize_SuccessEnvelope_OmitsErrorBranch()
    {
        var response = new DaemonResponse(
            Id: "abc",
            Data: new { value = 1 },
            Error: null,
            Meta: null);

        var json = JsonOutput.Serialize(response);

        Assert.Contains("\"data\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ErrorEnvelope_OmitsDataBranch()
    {
        var response = new DaemonResponse(
            Id: "abc",
            Data: null,
            Error: new ErrorInfo("SOLUTION_UNAVAILABLE", "Solution is currently unavailable."),
            Meta: null);

        var json = JsonOutput.Serialize(response);

        Assert.Contains("\"error\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", json, StringComparison.Ordinal);
    }
}
