using System.CommandLine;
using DotnetAICraft.Commands;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class ServerCommandTests
{
    [Fact]
    public void Build_ExposesExpectedSubcommandsAndOptions()
    {
        var command = ServerCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        Assert.Equal("server", command.Name);

        var start = AssertSingleSubcommand(command, "start");
        var stop = AssertSingleSubcommand(command, "stop");
        var status = AssertSingleSubcommand(command, "status");
        var reload = AssertSingleSubcommand(command, "reload");

        AssertContainsOption(start, "--solution");
        AssertContainsOption(start, "--idle-timeout");

        AssertContainsOption(stop, "--solution");
        AssertContainsOption(status, "--solution");

        AssertContainsOption(reload, "--solution");
        AssertContainsOption(reload, "--idle-timeout");
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution", "-s") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static Command AssertSingleSubcommand(Command command, string name)
        => Assert.IsType<Command>(Assert.Single(command.Subcommands, c => c.Name == name));

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));
}
