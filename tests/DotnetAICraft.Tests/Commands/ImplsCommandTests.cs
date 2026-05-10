using System.CommandLine;
using DotnetAICraft.Commands;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class ImplsCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndAliases()
    {
        var command = ImplsCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        Assert.Equal("impls", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--symbol");
        AssertContainsOption(command, "--idle-timeout");
    }

    [Fact]
    public void Build_SymbolOption_IsRequired()
    {
        var command = ImplsCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());
        var symbolOption = GetOption<string>(command, "--symbol");

        Assert.True(symbolOption.Required);
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
