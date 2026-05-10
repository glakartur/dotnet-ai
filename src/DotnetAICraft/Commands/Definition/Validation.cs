namespace DotnetAICraft.Commands.Definition;

internal static class Validation
{
    internal static void ValidateCliArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        var hasSymbol = !string.IsNullOrWhiteSpace(symbol);
        var hasAnyLocation = file is not null || line is not null || col is not null;
        var hasCompleteLocation = file is not null && line is not null && col is not null;

        if (hasSymbol == hasAnyLocation)
            throw new ArgumentException(
                "Provide exactly one input mode: either --symbol OR --file --line --col");

        if (hasAnyLocation && !hasCompleteLocation)
            throw new ArgumentException(
                "Location mode requires all of --file --line --col");
    }

    internal static void ValidateDaemonArgs(string? symbol, string? file, int? line, int? col)
    {
        var hasSymbol = !string.IsNullOrWhiteSpace(symbol);
        var hasAnyLocation = !string.IsNullOrWhiteSpace(file) || line is not null || col is not null;
        var hasCompleteLocation = !string.IsNullOrWhiteSpace(file) && line is not null && col is not null;

        if (hasSymbol == hasAnyLocation)
            throw new ArgumentException(
                "Provide exactly one input mode: either 'symbol' OR 'file'+'line'+'col'.");

        if (hasAnyLocation && !hasCompleteLocation)
            throw new ArgumentException(
                "Location mode requires 'file', 'line', and 'col' parameters.");
    }
}
