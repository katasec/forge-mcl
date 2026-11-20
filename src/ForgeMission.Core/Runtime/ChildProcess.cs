using System.Diagnostics;
using System.Text;

namespace ForgeMission.Core.Runtime;

/// <summary>Output limits for one child process. <paramref name="Label"/> names the child in the
/// over-limit message ("{Label} stdout exceeds {limit} bytes.").</summary>
internal readonly record struct ChildProcessLimits(int MaxOutputBytes, int MaxErrorBytes, string Label);

/// <summary>
/// The one owner of a started child process's lifetime, shared by kind:exec experts and the
/// workspace terminal: writes stdin, reads bounded stdout/stderr, and on a timeout, the caller's
/// cancellation or a failed read kills the process tree and joins the child before returning or
/// throwing. The process must be started with stdin, stdout and stderr redirected.
/// </summary>
internal static class ChildProcess
{
    /// <summary>Writes <paramref name="input"/> to stdin, closes it, and reads bounded stdout/stderr
    /// until the child exits. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellation"/> fires and <see cref="IOException"/> when a stream fails or
    /// exceeds its limit; the child has exited in every case.</summary>
    internal static async Task<(string Output, string Error, int ExitCode)> ExchangeAsync(
        Process process, string input, ChildProcessLimits limits, CancellationTokenSource cancellation)
    {
        var write = WriteInputAsync(process.StandardInput.BaseStream, input, cancellation);
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, limits.MaxOutputBytes, $"{limits.Label} stdout", cancellation);
        var error = ReadBoundedAsync(process.StandardError.BaseStream, limits.MaxErrorBytes, $"{limits.Label} stderr", cancellation);
        var io = Task.WhenAll(write, output, error);
        try
        {
            await io;
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch
        {
            cancellation.Cancel();
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await io; }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            throw;
        }
        return (await output, await error, process.ExitCode);
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // already exited
    }

    private static async Task WriteInputAsync(Stream stream, string input, CancellationTokenSource cancellation)
    {
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellation.Token); }
        catch (IOException exception) when (DeclinedInput(exception, cancellation.Token)) { }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { cancellation.Cancel(); throw; }
        finally { stream.Dispose(); }
    }

    private static bool DeclinedInput(IOException exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (OperatingSystem.IsWindows())
            return exception.HResult is unchecked((int)0x8007006D) or unchecked((int)0x800700E8) or unchecked((int)0x800700E9);
        return exception.InnerException is System.Net.Sockets.SocketException
            { NativeErrorCode: 32, SocketErrorCode: System.Net.Sockets.SocketError.Shutdown };
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int limit, string name, CancellationTokenSource cancellation)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation.Token)) > 0)
            {
                if (bytes.Length + count > limit) throw new IOException($"{name} exceeds {limit} bytes.");
                bytes.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { cancellation.Cancel(); throw; }
    }
}
