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

internal sealed class ConsoleErrorCapture : IDisposable
{
    private readonly TextWriter _originalErr;
    private readonly StringWriter _writer;

    private ConsoleErrorCapture(TextWriter originalErr, StringWriter writer)
    {
        _originalErr = originalErr;
        _writer = writer;
    }

    public static ConsoleErrorCapture Start()
    {
        var originalErr = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        return new ConsoleErrorCapture(originalErr, writer);
    }

    public string GetOutput() => _writer.ToString();

    public void Dispose()
    {
        Console.SetError(_originalErr);
        _writer.Dispose();
    }
}
