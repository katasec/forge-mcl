namespace ForgeMission.Cli.Services;

// Signals a deliberate CLI exit with a specific process code. Thrown by Die and by commands that
// exit with a code of their own, instead of calling Environment.Exit deep in a helper, and
// caught once by the top-level handler in Program.cs. Routing the exit through an exception
// lets finally/teardown blocks run on the way out and keeps the exit path testable.
internal sealed class ForgeExitException(int exitCode) : Exception
{
    public int ExitCode { get; } = exitCode;
}
