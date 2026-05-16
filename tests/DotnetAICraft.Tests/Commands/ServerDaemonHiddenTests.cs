using System.CommandLine;
using DotnetAICraft.Commands;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class ServerDaemonHiddenTests
{
    [Fact]
    public void Build_RegistersDaemonSubcommandWithHiddenTrue()
    {
        var command = ServerCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        var daemon = Assert.IsType<Command>(Assert.Single(command.Subcommands, c => c.Name == "daemon"));

        Assert.True(daemon.Hidden);
        AssertContainsOption(daemon, "--solution");
        AssertContainsOption(daemon, "--idle-timeout");
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution", "-s") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));
}
