using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Commands.Refs;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class RefsCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndAliases()
    {
        var command = RefsCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        Assert.Equal("refs", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--file");
        AssertContainsOption(command, "--line");
        AssertContainsOption(command, "--col");
        AssertContainsOption(command, "--symbol");
        AssertContainsOption(command, "--idle-timeout");
    }

    [Fact]
    public void ValidateArgs_RejectsMissingModes()
    {
        AssertValidationFails(file: null, line: null, col: null, symbol: null);
        AssertValidationFails(file: new FileInfo("/tmp/Sample.cs"), line: 10, col: null, symbol: null);
    }

    [Fact]
    public void ValidateArgs_AllowsSymbolOrCompleteLocation()
    {
        AssertValidationSucceeds(file: null, line: null, col: null, symbol: "Demo.Sample");
        AssertValidationSucceeds(file: new FileInfo("/tmp/Sample.cs"), line: 10, col: 4, symbol: null);
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution", "-s") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));

    private static void AssertValidationFails(FileInfo? file, int? line, int? col, string? symbol)
    {
        Assert.Throws<ArgumentException>(() => Validation.ValidateCliArgs(file, line, col, symbol));
    }

    private static void AssertValidationSucceeds(FileInfo? file, int? line, int? col, string? symbol)
    {
        Validation.ValidateCliArgs(file, line, col, symbol);
    }
}
