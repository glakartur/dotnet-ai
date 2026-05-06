using System.CommandLine;
using DotnetAICraft.Commands;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class DiagnosticsCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndAliases()
    {
        var solutionOption = BuildSolutionOption();
        var idleTimeoutOption = BuildIdleTimeoutOption();

        var command = DiagnosticsCommand.Build(solutionOption, idleTimeoutOption);

        Assert.Equal("diagnostics", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--severity");
        AssertContainsOption(command, "--project");
        AssertContainsOption(command, "--file");
        AssertContainsOption(command, "--idle-timeout");
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
