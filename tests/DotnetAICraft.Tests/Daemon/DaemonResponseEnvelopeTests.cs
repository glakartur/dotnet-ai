using DotnetAICraft.Models;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public class DaemonResponseEnvelopeTests
{
    [Fact]
    public void Serialize_OkEnvelope_IncludesStatusAndOmitsError()
    {
        var response = new DaemonResponse(
            Id: "abc",
            Status: DaemonResponseStatus.Ok,
            Result: new { value = 1 },
            Error: null,
            Meta: null);

        var json = JsonOutput.Serialize(response);

        Assert.Contains("\"status\":\"ok\"", json, StringComparison.Ordinal);
        Assert.Contains("\"result\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ProblemEnvelope_RequiresError()
    {
        var response = new DaemonResponse(
            Id: "abc",
            Status: DaemonResponseStatus.Problem,
            Result: null,
            Error: new ErrorInfo("SOLUTION_UNAVAILABLE", "Solution is currently unavailable."),
            Meta: null);

        var json = JsonOutput.Serialize(response);

        Assert.Contains("\"status\":\"problem\"", json, StringComparison.Ordinal);
        Assert.Contains("\"error\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"result\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_DebugNull_OmitsDebug()
    {
        var request = new DaemonRequest(
            Id: "req",
            Command: "symbols",
            Params: new { pattern = "*" },
            Debug: null,
            IdleTimeoutMinutes: null,
            Page: null);

        var json = JsonOutput.Serialize(request);

        Assert.DoesNotContain("\"debug\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_PaginatedResponse_UsesPageContractWithoutNextOffset()
    {
        var response = new DaemonResponse(
            Id: "abc",
            Status: DaemonResponseStatus.Ok,
            Result: new[] { "item" },
            Error: null,
            Debug: null,
            Page: new PageResponse(Offset: 10, Limit: 5, HasMore: true),
            Meta: null);

        var json = JsonOutput.Serialize(response);

        Assert.Contains("\"page\"", json, StringComparison.Ordinal);
        Assert.Contains("\"offset\":10", json, StringComparison.Ordinal);
        Assert.Contains("\"limit\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"hasMore\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nextOffset\"", json, StringComparison.Ordinal);
    }
}
