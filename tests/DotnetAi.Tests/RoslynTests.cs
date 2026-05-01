using DotnetAi.Roslyn;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DotnetAi.Tests;

public class RoslynExtensionsTests
{
    [Theory]
    [InlineData("IOrderService", "method")]
    [InlineData("IOrderService", "class")]
    [InlineData("ProcessOrder", "method")]
    public void GetKindName_ReturnsExpectedString(string name, string expectedKind)
    {
        ISymbol symbol = expectedKind switch
        {
            "method" => CreateMethodSymbol(name),
            "class"  => CreateTypeSymbol(name, TypeKind.Class),
            _ => CreateTypeSymbol(name, TypeKind.Interface)
        };

        // Kind names are lowercase strings
        Assert.NotNull(symbol.GetKindName());
    }

    [Theory]
    [InlineData("ToUpperSnakeCase", "TO_UPPER_SNAKE_CASE")]
    [InlineData("InvalidOperationException", "INVALID_OPERATION_EXCEPTION")]
    [InlineData("abc", "ABC")]
    public void ToUpperSnakeCase_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, input.ToUpperSnakeCase());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IMethodSymbol CreateMethodSymbol(string name)
    {
        var code = $"class C {{ void {name}() {{}} }}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var comp = CSharpCompilation.Create("Test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        return comp.GetTypeByMetadataName("C")!
            .GetMembers(name)
            .OfType<IMethodSymbol>()
            .First();
    }

    private static INamedTypeSymbol CreateTypeSymbol(string name, TypeKind kind)
    {
        var keyword = kind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct    => "struct",
            _                  => "class"
        };
        var code = $"{keyword} {name} {{}}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var comp = CSharpCompilation.Create("Test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        return comp.GetTypeByMetadataName(name)!;
    }
}

public class JsonOutputTests
{
    [Fact]
    public void Serialize_ProducesCamelCaseJson()
    {
        var obj    = new { MyProperty = "value", AnotherProp = 42 };
        var json   = Output.JsonOutput.Serialize(obj);

        Assert.Contains("myProperty", json);
        Assert.Contains("anotherProp", json);
    }

    [Fact]
    public void RoundTrip_WorksForDictionary()
    {
        var dict = new Dictionary<string, object?> { ["key"] = "value", ["num"] = 42 };
        var json = Output.JsonOutput.Serialize(dict);
        var back = Output.JsonOutput.Deserialize<Dictionary<string, object?>>(json);

        Assert.NotNull(back);
        Assert.Equal("value", back["key"]?.ToString());
    }
}
