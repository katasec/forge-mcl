using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeMission.Core.Adapters;

internal sealed class WindowsExecProcess : ExecProcess
{
    private IntPtr _job;
    private IntPtr _process;
    private IntPtr _thread;
    protected override bool Started => _process != IntPtr.Zero;

    protected override void StartNative(ProcessStartInfo options)
    {
        _job = WindowsNative.CreateJob(IntPtr.Zero, IntPtr.Zero);
        WindowsNative.Check(_job != IntPtr.Zero, "create owned job");
        var limits = new WindowsNative.ExtendedLimits { Basic = new() { Flags = 0x2000 } };
        WindowsNative.Check(WindowsNative.SetJob(_job, 9, ref limits, Marshal.SizeOf<WindowsNative.ExtendedLimits>()), "set kill-on-close job");
        var allocations = new List<IntPtr>();
        var attributes = IntPtr.Zero;
        try
        {
            attributes = CreateAttributes(allocations);
            var startup = Startup(attributes);
            var environment = string.Join('\0', options.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => $"{e.Key}={e.Value}")) + "\0\0";
            var block = Marshal.StringToHGlobalUni(environment);
            allocations.Add(block);
            var created = WindowsNative.CreateProcess(null, new StringBuilder(ExecProcessArguments.WindowsCommandLine(options)),
                IntPtr.Zero, IntPtr.Zero, true, 0x80000 | 0x400, block, options.WorkingDirectory, ref startup, out var process);
            if (!created) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            _process = process.Process;
            _thread = process.Thread;
            Close(ref _thread, "close launch thread");
        }
        finally
        {
            if (attributes != IntPtr.Zero) WindowsNative.DeleteAttributes(attributes);
            foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation);
        }
    }

    internal override async Task ObserveExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = WindowsNative.Wait(_process, 0);
            if (state == 0) return;
            if (state != 258) throw WindowsNative.Failure("observe owned process");
            await Task.Delay(10, cancellationToken);
        }
    }

    internal override void Terminate() => WindowsNative.Check(WindowsNative.TerminateJob(_job, 137), "terminate owned job");

    internal override async Task<int> JoinAsync(CancellationToken cleanupToken)
    {
        try
        {
            await ObserveExitAsync(cleanupToken);
            while (true)
            {
                WindowsNative.Check(WindowsNative.QueryJob(_job, 1, out var accounting, Marshal.SizeOf<WindowsNative.Accounting>(), IntPtr.Zero), "observe owned job");
                if (accounting.ActiveProcesses == 0) break;
                await Task.Delay(10, cleanupToken);
            }
            WindowsNative.Check(WindowsNative.ExitCode(_process, out var code), "read owned exit status");
            return unchecked((int)code);
        }
        catch (OperationCanceledException exception)
        { throw new ExecProcessCleanupException("bounded job join", exception); }
    }

    protected override void CloseNative()
    {
        List<Exception> failures = [];
        try { Close(ref _thread, "close thread"); } catch (IOException exception) { failures.Add(exception); }
        try { Close(ref _process, "close process"); } catch (IOException exception) { failures.Add(exception); }
        try { Close(ref _job, "close job"); } catch (IOException exception) { failures.Add(exception); }
        if (failures.Count > 0) throw new ExecProcessCleanupException("close native handles", new AggregateException(failures));
    }

    private IntPtr CreateAttributes(List<IntPtr> allocations)
    {
        UIntPtr size = UIntPtr.Zero;
        WindowsNative.InitializeAttributes(IntPtr.Zero, 2, 0, ref size);
        var list = Marshal.AllocHGlobal(checked((int)size));
        allocations.Add(list);
        WindowsNative.Check(WindowsNative.InitializeAttributes(list, 2, 0, ref size), "initialize launch attributes");
        try
        {
            AddAttribute(list, 0x2000D, [_job], allocations);
            AddAttribute(list, 0x20002, [Input.ClientSafePipeHandle.DangerousGetHandle(), Output.ClientSafePipeHandle.DangerousGetHandle(), Error.ClientSafePipeHandle.DangerousGetHandle()], allocations);
            return list;
        }
        catch { WindowsNative.DeleteAttributes(list); throw; }
    }

    private static void AddAttribute(IntPtr list, uint kind, IntPtr[] values, List<IntPtr> allocations)
    {
        var pointer = Marshal.AllocHGlobal(values.Length * IntPtr.Size);
        allocations.Add(pointer);
        Marshal.Copy(values, 0, pointer, values.Length);
        WindowsNative.Check(WindowsNative.UpdateAttribute(list, 0, kind, pointer, (UIntPtr)(values.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero), "set atomic launch attribute");
    }

    private WindowsNative.StartupInfoEx Startup(IntPtr attributes) => new()
    {
        Attributes = attributes,
        Info = new()
        {
            Size = Marshal.SizeOf<WindowsNative.StartupInfoEx>(), Flags = 0x100,
            Input = Input.ClientSafePipeHandle.DangerousGetHandle(),
            Output = Output.ClientSafePipeHandle.DangerousGetHandle(),
            Error = Error.ClientSafePipeHandle.DangerousGetHandle(),
        },
    };

    private static void Close(ref IntPtr handle, string operation)
    {
        if (handle == IntPtr.Zero) return;
        WindowsNative.Check(WindowsNative.Close(handle), operation);
        handle = IntPtr.Zero;
    }
}
