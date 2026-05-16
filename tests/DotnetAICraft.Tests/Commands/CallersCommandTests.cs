using System.CommandLine;
using DotnetAICraft.Commands;
using DotnetAICraft.Daemon;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class CallersCommandTests
{
    [Fact]
    public void Build_ExposesDirectionAndDepthOptions()
    {
        var command = CallersCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption(), formatOption: BuildFormatOption());

        Assert.Equal("callers", command.Name);
        AssertContainsOption(command, "--solution");
        AssertContainsOption(command, "--file");
        AssertContainsOption(command, "--line");
        AssertContainsOption(command, "--col");
        AssertContainsOption(command, "--symbol");
        AssertContainsOption(command, "--direction");
        AssertContainsOption(command, "--depth");
        AssertContainsOption(command, "--idle-timeout");
        AssertContainsOption(command, "--format");
    }

    private static Option<OutputFormat> BuildFormatOption()
        => new("--format") { DefaultValueFactory = _ => OutputFormat.Text };

    [Fact]
    public void Parse_UsesDefaultDirectionAndDepth()
    {
        var command = CallersCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        var parseResult = command.Parse([
            "--solution", "/tmp/sample.sln",
            "--symbol", "Demo.CallGraphSample.C"]);

        var directionOption = GetOption<string>(command, "--direction");
        var depthOption = GetOption<int>(command, "--depth");

        Assert.Empty(parseResult.Errors);
        Assert.Equal(DaemonServer.CallGraphDefaultDirection, parseResult.GetValue(directionOption));
        Assert.Equal(DaemonServer.CallGraphDefaultDepth, parseResult.GetValue(depthOption));
    }

    [Fact]
    public void Parse_UsesProvidedDirectionAndDepth()
    {
        var command = CallersCommand.Build(BuildSolutionOption(), BuildIdleTimeoutOption());

        var parseResult = command.Parse([
            "--solution", "/tmp/sample.sln",
            "--symbol", "Demo.CallGraphSample.C",
            "--direction", "both",
            "--depth", "3"]);

        var directionOption = GetOption<string>(command, "--direction");
        var depthOption = GetOption<int>(command, "--depth");

        Assert.Empty(parseResult.Errors);
        Assert.Equal("both", parseResult.GetValue(directionOption));
        Assert.Equal(3, parseResult.GetValue(depthOption));
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
