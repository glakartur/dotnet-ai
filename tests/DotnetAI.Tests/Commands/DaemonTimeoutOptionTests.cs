using System.CommandLine;
using DotnetAi.Commands;
using Xunit;

namespace DotnetAi.Tests.Commands;

public class DaemonTimeoutOptionTests
{
    [Fact]
    public void DaemonBackedCommands_ExposeIdleTimeoutOption()
    {
        var solutionOption = BuildSolutionOption();
        var idleTimeoutOption = BuildIdleTimeoutOption();

        var refs = RefsCommand.Build(solutionOption, idleTimeoutOption);
        var rename = RenameCommand.Build(solutionOption, idleTimeoutOption);
        var impls = ImplsCommand.Build(solutionOption, idleTimeoutOption);
        var callers = CallersCommand.Build(solutionOption, idleTimeoutOption);
        var symbols = SymbolsCommand.Build(solutionOption, idleTimeoutOption);
        var server = ServerCommand.Build(solutionOption, idleTimeoutOption);

        AssertContainsOption(refs, "--idle-timeout");
        AssertContainsOption(rename, "--idle-timeout");
        AssertContainsOption(impls, "--idle-timeout");
        AssertContainsOption(callers, "--idle-timeout");
        AssertContainsOption(symbols, "--idle-timeout");

        var start = server.Subcommands.Single(c => c.Name == "start");
        var reload = server.Subcommands.Single(c => c.Name == "reload");
        AssertContainsOption(start, "--idle-timeout");
        AssertContainsOption(reload, "--idle-timeout");
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
