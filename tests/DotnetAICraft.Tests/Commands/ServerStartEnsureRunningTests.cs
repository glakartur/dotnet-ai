using System.Text.Json;
using DotnetAICraft.Tests.Support;
using ServerEntry = DotnetAICraft.Commands.Server.Entry;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

[Collection("Console output")]
public class ServerStartEnsureRunningTests
{
    [Fact]
    public async Task StartAsync_WithInvalidIdleTimeout_ReturnsInvalidIdleTimeoutError()
    {
        string output;
        using (var capture = ConsoleOutputCapture.Start())
        {
            await ServerEntry.StartAsync(CreateUniqueSolutionPath(), idleTimeout: "garbage", format: DotnetAICraft.Output.OutputFormat.Json);
            output = capture.GetOutput();
        }

        using var json = JsonDocument.Parse(output);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("INVALID_IDLE_TIMEOUT", error.GetProperty("code").GetString());
    }

    private static string CreateUniqueSolutionPath()
        => Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-ensurerun-{Guid.NewGuid():N}.sln");
}
