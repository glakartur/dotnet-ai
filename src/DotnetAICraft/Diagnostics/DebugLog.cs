using System.Globalization;
using System.Threading;

namespace DotnetAICraft.Diagnostics;

public static class DebugLog
{
    private static int _enabled;

    public static bool IsEnabled => Volatile.Read(ref _enabled) == 1;

    public static void Configure(bool enabled)
        => Interlocked.Exchange(ref _enabled, enabled ? 1 : 0);

    public static void ConfigureFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("DOTNET_AICRAFT_DEBUG");
        if (string.IsNullOrWhiteSpace(raw))
            return;

        if (bool.TryParse(raw, out var parsed))
        {
            Configure(parsed);
            return;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            Configure(parsedInt != 0);
    }

    public static void ConfigureFromArgs(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--debug", StringComparison.Ordinal))
            {
                Configure(true);
                return;
            }
        }
    }

    public static void Write(string component, string message)
    {
        if (!IsEnabled)
            return;

        Console.Error.WriteLine($"[dotnet-aicraft debug {DateTime.UtcNow:O}] [{component}] {message}");
    }
}
