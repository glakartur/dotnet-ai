namespace DotnetAICraft.Roslyn;

public static class PathFormatter
{
    /// <summary>
    /// Returns <paramref name="absolutePath"/> made relative to <paramref name="solutionDir"/> with
    /// forward-slash separators. Falls back to a forward-slash-normalized absolute path when the
    /// input is on a different volume, would escape the solution tree (<c>..</c> prefix), or any
    /// I/O-free relativization would otherwise fail. Null or empty input is returned unchanged.
    /// </summary>
    public static string? ToRelative(string? absolutePath, string solutionDir)
    {
        if (absolutePath is null) return null;
        if (absolutePath.Length == 0) return string.Empty;

        if (string.IsNullOrEmpty(solutionDir))
            return absolutePath.Replace('\\', '/');

        string relative;
        try
        {
            relative = Path.GetRelativePath(solutionDir, absolutePath);
        }
        catch
        {
            return absolutePath.Replace('\\', '/');
        }

        // Out-of-tree (`..` prefix) or cross-volume (GetRelativePath returns the original absolute).
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return absolutePath.Replace('\\', '/');

        return relative.Replace('\\', '/');
    }
}
