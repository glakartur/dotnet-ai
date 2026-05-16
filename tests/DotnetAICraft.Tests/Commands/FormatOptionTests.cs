using System.CommandLine;
using System.CommandLine.Parsing;
using DotnetAICraft.Output;
using Xunit;

namespace DotnetAICraft.Tests.Commands;

public class FormatOptionTests
{
    [Fact]
    public void Parses_Text()
    {
        var (root, opt) = BuildRoot();
        var parse = root.Parse(["dummy", "--format", "text"]);
        Assert.Empty(parse.Errors);
        Assert.Equal(OutputFormat.Text, parse.GetValue(opt));
    }

    [Fact]
    public void Parses_Json()
    {
        var (root, opt) = BuildRoot();
        var parse = root.Parse(["dummy", "--format", "json"]);
        Assert.Empty(parse.Errors);
        Assert.Equal(OutputFormat.Json, parse.GetValue(opt));
    }

    [Fact]
    public void Rejects_Yaml()
    {
        var (root, _) = BuildRoot();
        var parse = root.Parse(["dummy", "--format", "yaml"]);
        Assert.NotEmpty(parse.Errors);
    }

    [Fact]
    public void DefaultsToText_WhenOmitted()
    {
        var (root, opt) = BuildRoot();
        var parse = root.Parse(["dummy"]);
        Assert.Empty(parse.Errors);
        Assert.Equal(OutputFormat.Text, parse.GetValue(opt));
    }

    private static (RootCommand root, Option<OutputFormat> opt) BuildRoot()
    {
        var opt = new Option<OutputFormat>("--format")
        {
            DefaultValueFactory = _ => OutputFormat.Text,
            CustomParser = ParseOutputFormat
        };
        var sub = new Command("dummy");
        sub.Add(opt);
        sub.SetAction(_ => { });
        var root = new RootCommand();
        root.Add(sub);
        return (root, opt);
    }

    private static OutputFormat ParseOutputFormat(ArgumentResult result)
    {
        if (result.Tokens.Count == 0)
            return OutputFormat.Text;

        var raw = result.Tokens[0].Value;
        return raw.ToLowerInvariant() switch
        {
            "text" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            _ => InvalidFormat(result, raw)
        };
    }

    private static OutputFormat InvalidFormat(ArgumentResult result, string raw)
    {
        result.AddError($"Invalid --format value '{raw}'. Accepted values: text, json.");
        return OutputFormat.Text;
    }
}
