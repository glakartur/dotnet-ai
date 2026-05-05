using DotnetAICraft.Models;

namespace DotnetAICraft.Daemon;

public sealed record DaemonIdleTimeoutSetting(bool Enabled, TimeSpan Duration, string Normalized)
{
    public static readonly DaemonIdleTimeoutSetting Default =
        new(true, TimeSpan.FromMinutes(60), "60m");

    public static readonly DaemonIdleTimeoutSetting Off =
        new(false, Timeout.InfiniteTimeSpan, "off");
}

public static class DaemonIdleTimeoutParser
{
    private const string ErrorCode = "INVALID_IDLE_TIMEOUT";

    public static bool TryParseOptional(
        string? raw,
        out DaemonIdleTimeoutSetting? setting,
        out ErrorInfo? error)
    {
        setting = null;
        error = null;

        if (raw is null)
            return true;

        if (!TryParse(raw, out var parsed, out error))
            return false;

        setting = parsed;
        return true;
    }

    public static bool TryParse(
        string raw,
        out DaemonIdleTimeoutSetting setting,
        out ErrorInfo? error)
    {
        setting = DaemonIdleTimeoutSetting.Default;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = BuildError("Timeout cannot be empty.");
            return false;
        }

        var normalized = raw.Trim();
        if (normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            setting = DaemonIdleTimeoutSetting.Off;
            return true;
        }

        if (!TryParseDuration(normalized, out var duration))
        {
            error = BuildError(
                "Unsupported timeout format. Use 'off' or a positive duration like '5m' or '1h'.");
            return false;
        }

        if (duration <= TimeSpan.Zero)
        {
            error = BuildError("Timeout must be greater than zero.");
            return false;
        }

        setting = new DaemonIdleTimeoutSetting(true, duration, NormalizeDuration(normalized));
        return true;
    }

    private static string NormalizeDuration(string raw)
    {
        var unit = raw[^1].ToString().ToLowerInvariant();
        var value = raw[..^1];

        return $"{value}{unit}";
    }

    private static bool TryParseDuration(string raw, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        var valueSpan = raw.AsSpan();
        string unit;

        if (raw.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            unit = "m";
            valueSpan = valueSpan[..^1];
        }
        else if (raw.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            unit = "h";
            valueSpan = valueSpan[..^1];
        }
        else
        {
            return false;
        }

        if (!long.TryParse(valueSpan, out var value))
            return false;

        try
        {
            duration = unit switch
            {
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                _ => TimeSpan.Zero
            };
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return true;
    }

    private static ErrorInfo BuildError(string message)
        => new(
            ErrorCode,
            message,
            new
            {
                acceptedValues = "off | <positive duration with unit: m|h>",
                examples = new[] { "off", "5m", "1h" }
            });
}
