using System.CommandLine;

namespace ForgeMission.Cli.Commands;

// Every forge command sets its action here: System.CommandLine's asynchronous action, which is
// handed the Ctrl+C/SIGTERM token to pass to everything it awaits
// (learn.microsoft.com/dotnet/standard/commandline/how-to-parse-and-invoke#asynchronous-actions).
// As in Microsoft's sample, the action owns that token's cancellation and returns an exit code:
// 130, the Ctrl+C code System.CommandLine forces when an action outlives ProcessTerminationTimeout.
// Any other cancellation (a client timeout) still reaches Program's top-level handler.
internal static class CommandActions
{
    internal const int Interrupted = 130;

    internal static void SetCancellableAction(this Command command, Func<ParseResult, CancellationToken, Task<int>> action) =>
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try { return await action(parseResult, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Interrupted; }
        });

    /// <summary>An action without an exit code of its own exits 0, as System.CommandLine's does.</summary>
    internal static void SetCancellableAction(this Command command, Func<ParseResult, CancellationToken, Task> action) =>
        command.SetCancellableAction(async (parseResult, cancellationToken) =>
        {
            await action(parseResult, cancellationToken);
            return 0;
        });
}
