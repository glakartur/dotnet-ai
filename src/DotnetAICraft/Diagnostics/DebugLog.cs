using System.Globalization;
using System.Threading;

namespace DotnetAICraft.Diagnostics;

public static class DebugLog
{
    private static int _enabled;
    private static readonly AsyncLocal<DebugCaptureScope?> _currentScope = new();

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
        var line = $"[dotnet-aicraft debug {DateTime.UtcNow:O}] [{component}] {message}";

        if (IsEnabled)
            Console.Error.WriteLine(line);

        _currentScope.Value?.Append(line);
    }

    public static void WriteResponseDebug(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            Console.Error.WriteLine(line);
    }

    public static DebugCaptureScope BeginCapture()
    {
        var prior = _currentScope.Value;
        var scope = new DebugCaptureScope(prior);
        _currentScope.Value = scope;
        return scope;
    }

    public sealed class DebugCaptureScope : IDisposable
    {
        private readonly DebugCaptureScope? _prior;
        private readonly List<string> _lines = new();
        private readonly object _gate = new();
        private bool _disposed;

        internal DebugCaptureScope(DebugCaptureScope? prior)
        {
            _prior = prior;
        }

        internal void Append(string line)
        {
            lock (_gate)
                _lines.Add(line);
        }

        public string[] GetLines()
        {
            lock (_gate)
                return _lines.ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _currentScope.Value = _prior;
        }
    }
}
