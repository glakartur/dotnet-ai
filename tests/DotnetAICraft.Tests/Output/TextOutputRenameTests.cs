using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class TextOutputRenameTests
{
    [Fact]
    public void Happy_DryRunHeaderAndChangeRows()
    {
        var changes = new[]
        {
            new RenameChange("/a/F1.cs", 10, 4, "Old", "New"),
            new RenameChange("/a/F2.cs", 20, 8, "Old", "New"),
            new RenameChange("/a/F3.cs", 30, 1, "Old", "New"),
        };
        var result = new RenameResult("Demo.Old", "New", Applied: false, DryRun: true, Changes: changes);
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRename(result, "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("3 changes for Demo.Old -> New (dry-run) in S.sln", text);
        Assert.Contains("/a/F1.cs:10:4: Old -> New", text);
    }

    [Fact]
    public void Applied_HeaderUsesApplied()
    {
        var result = new RenameResult("X", "Y", Applied: true, DryRun: false,
            Changes: new[] { new RenameChange("/a/F.cs", 1, 1, "X", "Y") });
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRename(result, "S.sln");
        Assert.Contains("(applied)", cap.GetOutput());
    }

    [Fact]
    public void EmptyChanges_SingleHeaderLine()
    {
        var result = new RenameResult("X", "Y", Applied: false, DryRun: true, Changes: Array.Empty<RenameChange>());
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteRename(result, "S.sln");
        Assert.Equal($"0 changes for X -> Y (dry-run) in S.sln{Environment.NewLine}", cap.GetOutput());
    }
}
