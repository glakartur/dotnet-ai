using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DotnetAICraft.Output;

public static class JsonOutput
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions Options = new(WireOptions)
    {
        WriteIndented = true
    };

    public static void Write<T>(T data)
        => Console.WriteLine(JsonSerializer.Serialize(data, Options));

    public static void WriteError(string code, string message, object? details = null)
        => Write(new { error = new { code, message, details } });

    /// <summary>
    /// Writes <paramref name="data"/> wrapped in an object that surfaces the absolute solution
    /// root as the first property. Arrays are wrapped as <c>{ solutionRoot, items: [...] }</c>;
    /// objects gain <c>solutionRoot</c> as their first property; primitives or null fall back to
    /// <c>{ solutionRoot, value }</c>.
    /// </summary>
    public static void WriteWithSolutionRoot(string solutionRoot, object? data)
    {
        var envelope = new JsonObject { ["solutionRoot"] = solutionRoot };

        var node = data is null
            ? null
            : data is JsonElement element
                ? JsonNode.Parse(element.GetRawText())
                : JsonNode.Parse(JsonSerializer.Serialize(data, WireOptions));

        switch (node)
        {
            case JsonObject obj:
                foreach (var prop in obj.ToList())
                {
                    obj.Remove(prop.Key);
                    envelope[prop.Key] = prop.Value;
                }
                break;
            case JsonArray:
                envelope["items"] = node;
                break;
            default:
                envelope["value"] = node;
                break;
        }

        Console.WriteLine(envelope.ToJsonString(Options));
    }

    public static string Serialize<T>(T data)
        => JsonSerializer.Serialize(data, WireOptions);

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, WireOptions);

    public static T? Deserialize<T>(JsonElement element)
        => JsonSerializer.Deserialize<T>(element, WireOptions);
}
