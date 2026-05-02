using Microsoft.CodeAnalysis;

namespace DotnetAi.Roslyn;

public static class RoslynExtensions
{
    public static string GetKindName(this ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Class }     => "class",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { TypeKind: TypeKind.Struct }    => "struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum }      => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate }  => "delegate",
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol                                        => "method",
        IPropertySymbol                                      => "property",
        IFieldSymbol                                         => "field",
        IEventSymbol                                         => "event",
        INamespaceSymbol                                     => "namespace",
        ILocalSymbol                                         => "local",
        IParameterSymbol                                     => "parameter",
        _                                                    => symbol.Kind.ToString().ToLower()
    };

    public static string GetShortLocation(this Location location)
    {
        if (!location.IsInSource) return "<metadata>";
        var span = location.GetLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}";
    }

    public static (string File, int Line, int Col) GetFileLineCol(this Location location)
    {
        var span = location.GetLineSpan();
        return (
            span.Path ?? "",
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1
        );
    }

    public static string GetContextLine(this Location location)
    {
        if (!location.IsInSource) return "";
        try
        {
            var sourceText = location.SourceTree?.GetText();
            if (sourceText is null) return "";
            var linePos = location.GetLineSpan().StartLinePosition;
            return sourceText.Lines[linePos.Line].ToString().Trim();
        }
        catch { return ""; }
    }

    /// <summary>Convert PascalCase to UPPER_SNAKE_CASE for error codes.</summary>
    public static string ToUpperSnakeCase(this string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(s[i]));
        }
        return sb.ToString();
    }
}
