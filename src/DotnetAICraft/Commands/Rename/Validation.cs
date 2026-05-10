namespace DotnetAICraft.Commands.Rename;

internal static class Validation
{
    internal static void ValidateCliModeArgs(FileInfo? file, int? line, int? col, string? symbol)
    {
        if (symbol is null && (file is null || line is null || col is null))
        {
            throw new ArgumentException(
                "Provide either --symbol OR all of --file --line --col");
        }
    }
}
