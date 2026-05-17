using DotnetAICraft.Roslyn;
using Xunit;

namespace DotnetAICraft.Tests.Roslyn;

public class PathFormatterTests
{
    [Fact]
    public void ReturnsNull_WhenInputIsNull()
    {
        Assert.Null(PathFormatter.ToRelative(null, "/repo"));
    }

    [Fact]
    public void ReturnsEmpty_WhenInputIsEmpty()
    {
        Assert.Equal(string.Empty, PathFormatter.ToRelative(string.Empty, "/repo"));
    }

    [Fact]
    public void ReturnsForwardSlashRelative_ForFileInsideSolutionDir()
    {
        var solutionDir = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
        var absolute = OperatingSystem.IsWindows() ? @"C:\repo\Foo\Bar.cs" : "/repo/Foo/Bar.cs";

        var result = PathFormatter.ToRelative(absolute, solutionDir);

        Assert.Equal("Foo/Bar.cs", result);
    }

    [Fact]
    public void ReturnsBareFilename_ForFileInSameDir()
    {
        var solutionDir = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
        var absolute = OperatingSystem.IsWindows() ? @"C:\repo\File.cs" : "/repo/File.cs";

        Assert.Equal("File.cs", PathFormatter.ToRelative(absolute, solutionDir));
    }

    [Fact]
    public void ReturnsAbsoluteForwardSlash_ForFileOutsideTree()
    {
        var solutionDir = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
        var absolute = OperatingSystem.IsWindows() ? @"C:\elsewhere\X.cs" : "/elsewhere/X.cs";

        var result = PathFormatter.ToRelative(absolute, solutionDir);

        if (OperatingSystem.IsWindows())
            Assert.Equal("C:/elsewhere/X.cs", result);
        else
            Assert.Equal("/elsewhere/X.cs", result);
    }

    [Fact]
    public void ReturnsAbsoluteForwardSlash_ForFileOnDifferentVolume()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = PathFormatter.ToRelative(@"D:\other\X.cs", @"C:\repo");

        Assert.Equal("D:/other/X.cs", result);
    }

    [Fact]
    public void HandlesTrailingSeparatorOnSolutionDir()
    {
        var solutionDir = OperatingSystem.IsWindows() ? @"C:\repo\" : "/repo/";
        var absolute = OperatingSystem.IsWindows() ? @"C:\repo\Foo\Bar.cs" : "/repo/Foo/Bar.cs";

        Assert.Equal("Foo/Bar.cs", PathFormatter.ToRelative(absolute, solutionDir));
    }

    [Fact]
    public void FallsBackToForwardSlash_WhenSolutionDirIsEmpty()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\repo\Foo\Bar.cs" : "/repo/Foo/Bar.cs";

        var result = PathFormatter.ToRelative(absolute, string.Empty);

        Assert.Equal(absolute.Replace('\\', '/'), result);
    }
}
