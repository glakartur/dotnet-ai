using System.CommandLine;
using DotnetAICraft.Commands;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class RenameCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndAliases()
    {
        var command = RenameCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        Assert.Equal("rename", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--file");
        AssertContainsOption(command, "--line");
        AssertContainsOption(command, "--col");
        AssertContainsOption(command, "--symbol");
        AssertContainsOption(command, "--to");
        AssertContainsOption(command, "--dry-run");
        AssertContainsOption(command, "--idle-timeout");
    }

    [Fact]
    public void Build_ToOption_IsRequired()
    {
        var command = RenameCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());
        var toOption = GetOption<string>(command, "--to");

        Assert.True(toOption.Required);
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution", "-s") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static Option<T> GetOption<T>(Command command, string alias)
        => Assert.IsType<Option<T>>(command.Options.Single(opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias)));

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));
}
