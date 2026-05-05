using System.Text.Json;
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

    public static string Serialize<T>(T data)
        => JsonSerializer.Serialize(data, WireOptions);

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, WireOptions);
}
