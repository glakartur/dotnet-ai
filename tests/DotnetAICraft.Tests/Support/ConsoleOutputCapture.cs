namespace DotnetAICraft.Tests.Support;

internal sealed class ConsoleOutputCapture : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly StringWriter _writer;

    private ConsoleOutputCapture(TextWriter originalOut, StringWriter writer)
    {
        _originalOut = originalOut;
        _writer = writer;
    }

    public static ConsoleOutputCapture Start()
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        return new ConsoleOutputCapture(originalOut, writer);
    }

    public string GetOutput() => _writer.ToString();

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _writer.Dispose();
    }
}
