namespace ForgeMission.Tests.Cli;

// Console.Out, Console.Error and Console.In are process-wide. Tests that capture them share this
// collection, which runs on its own after the parallel ones, so no other test writes into a capture.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>Redirects the console for one test and restores it on dispose. Captured text uses
/// <c>\n</c> line endings.</summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _error = Console.Error;
    private readonly TextReader _in = Console.In;
    private readonly StringWriter _capturedOut = new();
    private readonly StringWriter _capturedError = new();

    public ConsoleCapture()
    {
        Console.SetOut(_capturedOut);
        Console.SetError(_capturedError);
        Console.SetIn(new StringReader(""));
    }

    public string Out => _capturedOut.ToString().ReplaceLineEndings("\n");

    public string Error => _capturedError.ToString().ReplaceLineEndings("\n");

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
        Console.SetIn(_in);
    }
}
