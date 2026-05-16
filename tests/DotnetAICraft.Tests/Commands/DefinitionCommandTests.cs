using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Commands.Definition;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class DefinitionCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndAliases()
    {
        var solutionOption = BuildSolutionOption();
        var idleTimeoutOption = BuildIdleTimeoutOption();

        var command = DefinitionCommand.Build(solutionOption, idleTimeoutOption, formatOption: BuildFormatOption());

        Assert.Equal("definition", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--file");
        AssertContainsOption(command, "--line");
        AssertContainsOption(command, "--col");
        AssertContainsOption(command, "--symbol");
        AssertContainsOption(command, "--idle-timeout");
        AssertContainsOption(command, "--format");
    }

    private static Option<OutputFormat> BuildFormatOption()
        => new("--format") { DefaultValueFactory = _ => OutputFormat.Text };

    [Fact]
    public void ValidateArgs_RejectsMissingMixedOrPartialInputModes()
    {
        AssertValidationFails(file: null, line: null, col: null, symbol: null);
        AssertValidationFails(file: new FileInfo("/tmp/Sample.cs"), line: 10, col: 4, symbol: "Demo.Sample");
        AssertValidationFails(file: new FileInfo("/tmp/Sample.cs"), line: 10, col: null, symbol: null);
    }

    [Fact]
    public void ValidateArgs_AllowsExactlyOneInputMode()
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
