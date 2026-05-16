using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class UnusedCommandTests
{
    [Fact]
    public void Build_ExposesExpectedOptionsAndDefaults()
    {
        var command = UnusedCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption(), formatOption: BuildFormatOption());

        Assert.Equal("unused", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "-s");
        AssertContainsOption(command, "--kind");
        AssertContainsOption(command, "--project");
        AssertContainsOption(command, "--public-only");
        AssertContainsOption(command, "--include-generated");
        AssertContainsOption(command, "--idle-timeout");
        AssertContainsOption(command, "--format");

        var parseResult = command.Parse([
            "--solution", "/tmp/sample.sln"]);

        var kindOption = GetOption<string>(command, "--kind");
        var publicOnlyOption = GetOption<bool>(command, "--public-only");
        var includeGeneratedOption = GetOption<bool>(command, "--include-generated");

        Assert.Empty(parseResult.Errors);
        Assert.Equal("all", parseResult.GetValue(kindOption));
        Assert.False(parseResult.GetValue(publicOnlyOption));
        Assert.False(parseResult.GetValue(includeGeneratedOption));
    }

    [Fact]
    public void Parse_BooleanFlags_SetExpectedValues()
    {
        var command = UnusedCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        var parseResult = command.Parse([
            "--solution", "/tmp/sample.sln",
            "--public-only",
            "--include-generated"]);

        var publicOnlyOption = GetOption<bool>(command, "--public-only");
        var includeGeneratedOption = GetOption<bool>(command, "--include-generated");

        Assert.Empty(parseResult.Errors);
        Assert.True(parseResult.GetValue(publicOnlyOption));
        Assert.True(parseResult.GetValue(includeGeneratedOption));
    }

    private static Option<FileInfo> BuildSolutionOption()
        => new("--solution", "-s") { Required = true };

    private static Option<string?> BuildIdleTimeoutOption()
        => new("--idle-timeout");

    private static Option<OutputFormat> BuildFormatOption()
        => new("--format") { DefaultValueFactory = _ => OutputFormat.Text };

    private static Option<T> GetOption<T>(Command command, string alias)
        => Assert.IsType<Option<T>>(command.Options.Single(opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias)));

    private static void AssertContainsOption(Command command, string alias)
        => Assert.Contains(command.Options, opt =>
            string.Equals(opt.Name, alias, StringComparison.Ordinal) ||
            opt.Aliases.Contains(alias));
}
