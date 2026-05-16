using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Commands.Shared;
using DotnetAICraft.Tests.Support;
using ServerEntry = DotnetAICraft.Commands.Server.Entry;
using SymbolsEntry = DotnetAICraft.Commands.Symbols.Entry;
using DotnetAICraft.Daemon;
using System.Text.Json;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

[Collection("Console output")]
public class DaemonTimeoutOptionTests
{
    private static readonly SemaphoreSlim SocketArtifactLock = new(1, 1);

    [Fact]
    public void DaemonBackedCommands_ExposeIdleTimeoutAndDebugOptions()
    {
        var solutionOption = BuildSolutionOption();
        var idleTimeoutOption = BuildIdleTimeoutOption();
        var debugOption = BuildDebugOption();

        var refs = RefsCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var definition = DefinitionCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var rename = RenameCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var impls = ImplsCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var callers = CallersCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var symbols = SymbolsCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var unused = UnusedCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var diagnostics = DiagnosticsCommand.Build(solutionOption, idleTimeoutOption, debugOption);
        var server = ServerCommand.Build(solutionOption, idleTimeoutOption, debugOption);

        AssertContainsOption(refs, "--idle-timeout");
        AssertContainsOption(refs, "--debug");
        AssertContainsOption(definition, "--idle-timeout");
        AssertContainsOption(definition, "--debug");
        AssertContainsOption(rename, "--idle-timeout");
        AssertContainsOption(rename, "--debug");
        AssertContainsOption(impls, "--idle-timeout");
        AssertContainsOption(impls, "--debug");
        AssertContainsOption(callers, "--idle-timeout");
        AssertContainsOption(callers, "--debug");
        AssertContainsOption(symbols, "--idle-timeout");
        AssertContainsOption(symbols, "--debug");
        AssertContainsOption(unused, "--idle-timeout");
        AssertContainsOption(unused, "--debug");
        AssertContainsOption(diagnostics, "--idle-timeout");
        AssertContainsOption(diagnostics, "--debug");

        var start = server.Subcommands.Single(c => c.Name == "start");
        var stop = server.Subcommands.Single(c => c.Name == "stop");
        var status = server.Subcommands.Single(c => c.Name == "status");
        var reload = server.Subcommands.Single(c => c.Name == "reload");
        AssertContainsOption(start, "--idle-timeout");
        AssertContainsOption(start, "--debug");
        AssertContainsOption(stop, "--debug");
        AssertContainsOption(status, "--debug");
        AssertContainsOption(reload, "--idle-timeout");
        AssertContainsOption(reload, "--debug");
    }

    [Fact]
    public async Task ConnectOrWriteValidationErrorAsync_WithDirectorySocketArtifact_WritesInvalidTypeError()
    {
        var solutionPath = Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        await SocketArtifactLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(socketPath);

            try
            {
                DaemonClient? client;
                string output;

                using (var capture = ConsoleOutputCapture.Start())
                {
                    client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout: null);
                    output = capture.GetOutput();
                }

                Assert.Null(client);
                Assert.False(string.IsNullOrWhiteSpace(output));

                using var json = JsonDocument.Parse(output);
                var error = json.RootElement.GetProperty("error");
                Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", error.GetProperty("code").GetString());

                var details = error.GetProperty("details");
                Assert.Equal("start", details.GetProperty("stage").GetString());
                Assert.Equal("directory", details.GetProperty("artifactType").GetString());
                Assert.Equal("invalidArtifactType", details.GetProperty("reasonCode").GetString());
                Assert.True(details.TryGetProperty("artifactName", out _));
                Assert.False(details.TryGetProperty("socketPath", out _));
            }
            finally
            {
                if (Directory.Exists(socketPath))
                    Directory.Delete(socketPath);
            }
        }
        finally
        {
            SocketArtifactLock.Release();
        }
    }

    [Fact]
    public async Task ServerStartAndSymbolsFlow_WithDirectorySocketArtifact_ReturnSameInvalidTypeErrorContract()
    {
        var solutionPath = Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        await SocketArtifactLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(socketPath);

            try
            {
                var serverJson = await CaptureJsonOutputAsync(() => ServerEntry.StartAsync(solutionPath, idleTimeout: null));
                var symbolsJson = await CaptureJsonOutputAsync(() => SymbolsEntry.ExecuteAsync(
                    solutionPath,
                    pattern: "Any*",
                    kind: "all",
                    limit: 1,
                    offset: 0,
                    idleTimeout: null));

                AssertMatchingInvalidTypeError(serverJson, expectedStage: "start");
                AssertMatchingInvalidTypeError(symbolsJson, expectedStage: "start");
            }
            finally
            {
                if (Directory.Exists(socketPath))
                    Directory.Delete(socketPath);
            }
        }
        finally
        {
            SocketArtifactLock.Release();
        }
    }

    [Fact]
    public async Task SendOrWriteValidationErrorAsync_WhenClientValidationFails_WritesStructuredError()
    {
        string output;
        using (var capture = ConsoleOutputCapture.Start())
        {
            var response = await CommandHelpers.SendOrWriteValidationErrorAsync(() =>
                throw new DaemonClientValidationException(
                    new DotnetAICraft.Models.ErrorInfo(
                        "DAEMON_RESPONSE_TIMEOUT",
                        "Timed out waiting for daemon response.",
                        new { command = "symbols" })));

            Assert.Null(response);
            output = capture.GetOutput();
        }

        using var json = JsonDocument.Parse(output);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("DAEMON_RESPONSE_TIMEOUT", error.GetProperty("code").GetString());
    }

    private static void AssertMatchingInvalidTypeError(string json, string expectedStage)
    {
        using var doc = JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE", error.GetProperty("code").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(expectedStage, details.GetProperty("stage").GetString());
        Assert.Equal("directory", details.GetProperty("artifactType").GetString());
        Assert.Equal("invalidArtifactType", details.GetProperty("reasonCode").GetString());
        Assert.True(details.TryGetProperty("artifactName", out _));
        Assert.False(details.TryGetProperty("socketPath", out _));

        var hasRemediation = details.TryGetProperty("remediation", out var remediation);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(hasRemediation);
            Assert.NotEqual(JsonValueKind.Null, remediation.ValueKind);
        }
        else
            Assert.False(hasRemediation);
    }

    private static async Task<string> CaptureJsonOutputAsync(Func<Task> operation)
    {
        using var capture = ConsoleOutputCapture.Start();
        await operation();
        return capture.GetOutput();
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static Option<bool> BuildDebugOption()
        => new("--debug");

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));
}
