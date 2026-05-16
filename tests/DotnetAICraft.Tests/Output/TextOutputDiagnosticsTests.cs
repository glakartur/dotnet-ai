using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Tests.Support;
using Xunit;

namespace DotnetAICraft.Tests.Output;

[Collection("Console output")]
public class TextOutputDiagnosticsTests
{
    [Fact]
    public void Mixed_HeaderCountsErrorsAndWarnings_BodyRendersAll()
    {
        var items = new[]
        {
            new DiagnosticResult("P", "CS0001", "error",   "msg1", "/a/F.cs", 1, 2, null, null),
            new DiagnosticResult("P", "CS0002", "error",   "msg2", "/a/F.cs", 3, 4, null, null),
            new DiagnosticResult("P", "CS1000", "warning", "msg3", "/a/F.cs", 5, 6, null, null),
            new DiagnosticResult("P", "CS1001", "warning", "msg4", "/a/F.cs", 7, 8, null, null),
            new DiagnosticResult("P", "CS1002", "warning", "msg5", "/a/F.cs", 9, 10, null, null),
            new DiagnosticResult("P", "INFO01", "info",    "msg6", "/a/F.cs", 11, 12, null, null)
        };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteDiagnostics(items, "S.sln");
        var text = cap.GetOutput();
        Assert.StartsWith("2 errors, 3 warnings", text);
        Assert.Contains("error /a/F.cs:1:2 [CS0001]: msg1", text);
        Assert.Contains("info /a/F.cs:11:12 [INFO01]: msg6", text);
    }

    [Fact]
    public void AllWarnings_HeaderShows0Errors()
    {
        var items = new[]
        {
            new DiagnosticResult("P", "W1", "warning", "m", "/a/F.cs", 1, 1, null, null),
            new DiagnosticResult("P", "W2", "warning", "m", "/a/F.cs", 2, 1, null, null),
        };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteDiagnostics(items, "S.sln");
        Assert.StartsWith("0 errors, 2 warnings", cap.GetOutput());
    }

    [Fact]
    public void Empty_HeaderOnly()
    {
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteDiagnostics(Array.Empty<DiagnosticResult>(), "S.sln");
        Assert.Equal($"0 errors, 0 warnings{Environment.NewLine}", cap.GetOutput());
    }

    [Fact]
    public void ProjectLevel_NoFile_RendersProjectNameInsteadOfLocation()
    {
        var items = new[]
        {
            new DiagnosticResult("MyApp", "CS9999", "error", "Project broken", null, null, null, null, null),
        };
        using var cap = ConsoleOutputCapture.Start();
        TextOutput.WriteDiagnostics(items, "S.sln");
        var text = cap.GetOutput();
        Assert.Contains("error MyApp [CS9999]: Project broken", text);
    }
}
