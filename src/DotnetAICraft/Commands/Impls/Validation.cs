namespace DotnetAICraft.Commands.Impls;

internal static class Validation
{
    internal static void ValidateDaemonArgs(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Missing or invalid 'symbol' parameter.");
    }
}
