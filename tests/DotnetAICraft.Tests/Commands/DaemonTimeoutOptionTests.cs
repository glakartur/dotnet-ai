using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Commands.Shared;
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
        }
        finally
        {
            if (Directory.Exists(socketPath))
                Directory.Delete(socketPath);
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
