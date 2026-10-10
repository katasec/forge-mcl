using System.Diagnostics;
using System.IO.Pipes;

namespace ForgeMission.Core.Adapters;

// Own the native lifetime before executable code starts, through pipe completion and exact reap.
internal abstract class ExecProcess : IAsyncDisposable
{
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);
    protected readonly AnonymousPipeServerStream Input;
    protected readonly AnonymousPipeServerStream Output;
    protected readonly AnonymousPipeServerStream Error;

    internal Stream StandardInput => Input;
    internal Stream StandardOutput => Output;
    internal Stream StandardError => Error;
    protected abstract bool Started { get; }

    protected ExecProcess()
    {
        Input = Pipe(PipeDirection.Out);
        AnonymousPipeServerStream? output = null;
        try { Output = output = Pipe(PipeDirection.In); Error = Pipe(PipeDirection.In); }
        catch { output?.Dispose(); Input.Dispose(); throw; }
    }

    internal static ExecProcess Start(ProcessStartInfo options)
    {
        if (OperatingSystem.IsLinux() && Environment.ProcessId == 1)
            throw new InvalidOperationException("Executable execution requires an init parent; Linux PID 1 is unsupported.");
        ExecProcess process = OperatingSystem.IsWindows() ? new WindowsExecProcess() : new PosixExecProcess();
        try
        {
            process.StartNative(options);
            process.Input.DisposeLocalCopyOfClientHandle();
            process.Output.DisposeLocalCopyOfClientHandle();
            process.Error.DisposeLocalCopyOfClientHandle();
            return process;
        }
        catch (Exception original)
        {
            process.CleanupFailedLaunch(original);
            throw;
        }
    }

    protected abstract void StartNative(ProcessStartInfo options);
    internal abstract Task ObserveExitAsync(CancellationToken cancellationToken);
    internal abstract void Terminate();
    internal abstract Task<int> JoinAsync(CancellationToken cleanupToken);
    protected abstract void CloseNative();

    public ValueTask DisposeAsync()
    {
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        CloseNative();
        return ValueTask.CompletedTask;
    }

    private void CleanupFailedLaunch(Exception original)
    {
        List<Exception> failures = [];
        if (Started)
        {
            try { Terminate(); } catch (IOException failure) { failures.Add(failure); }
            using var deadline = new CancellationTokenSource(CleanupBudget);
            try { JoinAsync(deadline.Token).GetAwaiter().GetResult(); } catch (IOException failure) { failures.Add(failure); }
        }
        try { DisposeAsync().GetAwaiter().GetResult(); } catch (IOException failure) { failures.Add(failure); }
        if (failures.Count > 0) throw new ExecProcessCleanupException("launch cleanup", new AggregateException(failures), original);
    }

    private static AnonymousPipeServerStream Pipe(PipeDirection direction) => new(direction,
        OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None);
}

internal sealed class ExecProcessCleanupException : IOException
{
    internal ExecProcessCleanupException(string operation, Exception failure, Exception? original = null)
        : base($"Executable cleanup failed during {operation}: {failure.Message}",
            original is null ? failure : new AggregateException(original, failure)) { }
}
