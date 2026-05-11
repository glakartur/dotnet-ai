using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Commands.Shared;
using ServerEntry = DotnetAICraft.Commands.Server.Entry;
using SymbolsEntry = DotnetAICraft.Commands.Symbols.Entry;
using DotnetAICraft.Daemon;
using System.Text.Json;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class DaemonTimeoutOptionTests
{
    private static readonly SemaphoreSlim ConsoleCaptureLock = new(1, 1);

    [Fact]
    public void DaemonBackedCommands_ExposeIdleTimeoutOption()
    {
        var solutionOption = BuildSolutionOption();
        var idleTimeoutOption = BuildIdleTimeoutOption();

        var refs = RefsCommand.Build(solutionOption, idleTimeoutOption);
        var definition = DefinitionCommand.Build(solutionOption, idleTimeoutOption);
        var rename = RenameCommand.Build(solutionOption, idleTimeoutOption);
        var impls = ImplsCommand.Build(solutionOption, idleTimeoutOption);
        var callers = CallersCommand.Build(solutionOption, idleTimeoutOption);
        var symbols = SymbolsCommand.Build(solutionOption, idleTimeoutOption);
        var unused = UnusedCommand.Build(solutionOption, idleTimeoutOption);
        var diagnostics = DiagnosticsCommand.Build(solutionOption, idleTimeoutOption);
        var server = ServerCommand.Build(solutionOption, idleTimeoutOption);

        AssertContainsOption(refs, "--idle-timeout");
        AssertContainsOption(definition, "--idle-timeout");
        AssertContainsOption(rename, "--idle-timeout");
        AssertContainsOption(impls, "--idle-timeout");
        AssertContainsOption(callers, "--idle-timeout");
        AssertContainsOption(symbols, "--idle-timeout");
        AssertContainsOption(unused, "--idle-timeout");
        AssertContainsOption(diagnostics, "--idle-timeout");

        var start = server.Subcommands.Single(c => c.Name == "start");
        var reload = server.Subcommands.Single(c => c.Name == "reload");
        AssertContainsOption(start, "--idle-timeout");
        AssertContainsOption(reload, "--idle-timeout");
    }

    [Fact]
    public async Task ConnectOrWriteValidationErrorAsync_WithDirectorySocketArtifact_WritesInvalidTypeError()
    {
        var solutionPath = Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
        Directory.CreateDirectory(socketPath);

        try
        {
            DaemonClient? client;
            string output;

            await ConsoleCaptureLock.WaitAsync();
            try
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    client = await CommandHelpers.ConnectOrWriteValidationErrorAsync(solutionPath, idleTimeout: null);
                    output = writer.ToString();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
            finally
            {
                ConsoleCaptureLock.Release();
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

    [Fact]
    public async Task ServerStartAndSymbolsFlow_WithDirectorySocketArtifact_ReturnSameInvalidTypeErrorContract()
    {
        var solutionPath = Path.Combine(Path.GetTempPath(), $"dotnet-aicraft-test-{Guid.NewGuid():N}.sln");
        var socketPath = DaemonClient.GetSocketPath(solutionPath);
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

            AssertMatchingInvalidTypeError(serverJson, expectedStage: "liveness");
            AssertMatchingInvalidTypeError(symbolsJson, expectedStage: "start");
        }
        finally
        {
            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);
        }
    }

    [Fact]
    public async Task SendOrWriteValidationErrorAsync_WhenClientValidationFails_WritesStructuredError()
    {
        string output;

        await ConsoleCaptureLock.WaitAsync();
        try
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                var response = await CommandHelpers.SendOrWriteValidationErrorAsync(() =>
                    throw new DaemonClientValidationException(
                        new DotnetAICraft.Models.ErrorInfo(
                            "DAEMON_RESPONSE_TIMEOUT",
                            "Timed out waiting for daemon response.",
                            new { command = "symbols" })));

                Assert.Null(response);
                output = writer.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
        finally
        {
            ConsoleCaptureLock.Release();
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
        await ConsoleCaptureLock.WaitAsync();
        try
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                await operation();
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
        finally
        {
            ConsoleCaptureLock.Release();
        }
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));
}
