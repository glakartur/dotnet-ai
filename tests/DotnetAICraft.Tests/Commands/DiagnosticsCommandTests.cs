using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Daemon;
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

public class SymbolsCommandTests
{
    [Fact]
    public void Build_ExposesPaginationOptionsWithDefaultValues()
    {
        var command = SymbolsCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        Assert.Equal("symbols", command.Name);
        AssertContainsOption(command, "--limit");
        AssertContainsOption(command, "--offset");

        var parseResult = command.Parse([
            "--solution", "/tmp/sample.sln",
            "--pattern", "Pagination*"]);

        var limitOption = GetOption<int>(command, "--limit");
        var offsetOption = GetOption<int>(command, "--offset");

        Assert.Empty(parseResult.Errors);
        Assert.Equal(DaemonServer.SymbolsDefaultLimit, parseResult.GetValue(limitOption));
        Assert.Equal(DaemonServer.SymbolsDefaultOffset, parseResult.GetValue(offsetOption));
    }

    [Fact]
    public void Build_KindOptionDescription_ListsGranularKinds()
    {
        var command = SymbolsCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());
        var kindOption = GetOption<string>(command, "--kind");

        Assert.Contains("constructor", kindOption.Description, StringComparison.Ordinal);
        Assert.Contains("interface", kindOption.Description, StringComparison.Ordinal);
        Assert.Contains("event", kindOption.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UsesProvidedLimitAndOffsetValues()
    {
        var command = SymbolsCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        var parseResult = command.Parse([
            "--solution", "/tmp/sample.sln",
            "--pattern", "Pagination*",
            "--limit", "1",
            "--offset", "50"]);

        var limitOption = GetOption<int>(command, "--limit");
        var offsetOption = GetOption<int>(command, "--offset");

        Assert.Empty(parseResult.Errors);
        Assert.Equal(1, parseResult.GetValue(limitOption));
        Assert.Equal(50, parseResult.GetValue(offsetOption));
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
