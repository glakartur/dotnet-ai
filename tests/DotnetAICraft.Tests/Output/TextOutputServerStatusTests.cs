using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class TextOutputServerStatusTests
{
    [Fact]
    public void Full_RendersAllRows()
    {
        var status = new DaemonStatus(
            Running: true,
            SolutionPath: "/path/to/S.sln",
            Projects: 3,
            Documents: 42,
            LoadedAt: new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc),
            Uptime: TimeSpan.FromMinutes(5),
            LoadState: "loaded",
            LastLoadAttemptAt: new DateTime(2026, 5, 16, 11, 0, 0, DateTimeKind.Utc),
            LastLoadErrorCode: "ERR_X",
            LastLoadErrorMessage: "boom");

        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteServerStatus(status);
        var text = cap.GetOutput();
        Assert.Contains("/path/to/S.sln [loaded]", text);
        Assert.Contains("Running: true", text);
        Assert.Contains("Projects: 3", text);
        Assert.Contains("Documents: 42", text);
        Assert.Contains("LastLoadAttemptAt:", text);
        Assert.Contains("LastLoadError: ERR_X: boom", text);
    }

    [Fact]
    public void Minimal_OmitsErrorRows()
    {
        var status = new DaemonStatus(
            Running: true,
            SolutionPath: "/path/to/S.sln",
            Projects: 1,
            Documents: 1,
            LoadedAt: DateTime.UtcNow,
            Uptime: TimeSpan.FromSeconds(10),
            LoadState: "loaded",
            LastLoadAttemptAt: null,
            LastLoadErrorCode: null,
            LastLoadErrorMessage: null);

        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteServerStatus(status);
        var text = cap.GetOutput();
        Assert.DoesNotContain("LastLoadAttemptAt:", text);
        Assert.DoesNotContain("LastLoadError:", text);
    }
}
