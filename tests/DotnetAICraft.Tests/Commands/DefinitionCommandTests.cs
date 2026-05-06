using System.CommandLine;
using System.Reflection;
using DotnetAICraft.Commands;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class DefinitionCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndAliases()
    {
        var solutionOption = BuildSolutionOption();
        var idleTimeoutOption = BuildIdleTimeoutOption();

        var command = DefinitionCommand.Build(solutionOption, idleTimeoutOption);

        Assert.Equal("definition", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--file");
        AssertContainsOption(command, "--line");
        AssertContainsOption(command, "--col");
        AssertContainsOption(command, "--symbol");
        AssertContainsOption(command, "--idle-timeout");
    }

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
        var method = GetValidateArgsMethod();

        var ex = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [file, line, col, symbol]));

        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    private static void AssertValidationSucceeds(FileInfo? file, int? line, int? col, string? symbol)
    {
        var method = GetValidateArgsMethod();
        method.Invoke(null, [file, line, col, symbol]);
    }

    private static MethodInfo GetValidateArgsMethod()
        => typeof(DefinitionCommand).GetMethod("ValidateArgs", BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException("Could not locate DefinitionCommand.ValidateArgs method.");
}
