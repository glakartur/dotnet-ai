using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class ServerCommandTests
{
    [Fact]
    public void Build_ExposesExpectedSubcommandsAndOptions()
    {
        var command = ServerCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption(), formatOption: BuildFormatOption());

        Assert.Equal("server", command.Name);

        var start = AssertSingleSubcommand(command, "start");
        var stop = AssertSingleSubcommand(command, "stop");
        var status = AssertSingleSubcommand(command, "status");
        var reload = AssertSingleSubcommand(command, "reload");

        AssertContainsOption(start, "--solution");
        AssertContainsOption(start, "--idle-timeout");
        AssertContainsOption(start, "--format");

        AssertContainsOption(stop, "--solution");
        AssertContainsOption(stop, "--format");
        AssertContainsOption(status, "--solution");
        AssertContainsOption(status, "--format");

        AssertContainsOption(reload, "--solution");
        AssertContainsOption(reload, "--idle-timeout");
        AssertContainsOption(reload, "--format");
    }

    private static Option<OutputFormat> BuildFormatOption()
        => new("--format") { DefaultValueFactory = _ => OutputFormat.Text };

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
