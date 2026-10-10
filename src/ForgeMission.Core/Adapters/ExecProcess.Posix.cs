using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForgeMission.Core.Adapters;

internal sealed class PosixExecProcess : ExecProcess
{
    private int _pid;
    private bool _observed;
    private bool _identityLost;
    private bool _terminationRequested;
    private IOException? _pendingTerminationError;
    protected override bool Started => _pid != 0;

    protected override void StartNative(ProcessStartInfo options)
    {
        var path = ExecProcessArguments.ResolveUnixCommand(options.FileName);
        var attributes = Marshal.AllocHGlobal(PosixNative.AttributeSize);
        var actions = Marshal.AllocHGlobal(PosixNative.ActionSize);
        var allocations = new List<IntPtr>();
        var attributeReady = false;
        var actionReady = false;
        try
        {
            PosixNative.Check(PosixNative.AttributeInit(attributes), "spawn attributes");
            attributeReady = true;
            PosixNative.Check(PosixNative.ActionsInit(actions), "spawn actions");
            actionReady = true;
            ConfigureSpawn(options, attributes, actions, allocations);
            var arguments = Vector([options.FileName, .. options.ArgumentList], allocations);
            var environment = Vector(options.Environment.Select(e => $"{e.Key}={e.Value}"), allocations);
            PosixNative.Check(PosixNative.Spawn(out var pid, Text(path, allocations), actions, attributes, arguments, environment), "posix_spawn");
            _pid = pid;
        }
        finally
        {
            if (actionReady) PosixNative.Check(PosixNative.ActionsDestroy(actions), "destroy spawn actions");
            if (attributeReady) PosixNative.Check(PosixNative.AttributeDestroy(attributes), "destroy spawn attributes");
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
            foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation);
        }
    }

    internal override async Task ObserveExitAsync(CancellationToken cancellationToken)
    {
        while (!_observed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _observed = ObserveRoot();
            if (!_observed) await Task.Delay(10, cancellationToken);
        }
    }

    internal override void Terminate()
    {
        if (_pid == 0 || _terminationRequested) return;
        if (_identityLost) throw PosixNative.Failure("terminate lost process identity", 10);
        if (!_observed) _observed = ObserveRoot();
        var result = PosixNative.Kill(-_pid, 9);
        var error = Marshal.GetLastPInvokeError();
        if (result != 0 && error == 1 && OperatingSystem.IsMacOS() && _observed)
            _pendingTerminationError = PosixNative.Failure("kill owned group", error);
        else if (result != 0 && !(error == 3 && _observed)) throw PosixNative.Failure("kill owned group", error);
        _terminationRequested = true;
    }

    internal override async Task<int> JoinAsync(CancellationToken cleanupToken)
    {
        try
        {
            await ObserveExitAsync(cleanupToken);
            if (!_terminationRequested) throw PosixNative.Failure("join unterminated group", 0);
            if (OperatingSystem.IsMacOS()) await ObserveRootOnlyGroupAsync(cleanupToken);
            var result = PosixNative.Reap(_pid, out var status, 1);
            if (result != _pid) throw PosixNative.Failure("reap owned root", Marshal.GetLastPInvokeError());
            _pid = 0;
            return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        }
        catch (Exception exception) when (_pendingTerminationError is not null)
        { throw new ExecProcessCleanupException("pending group termination", new AggregateException(_pendingTerminationError, exception)); }
        catch (OperationCanceledException exception)
        { throw new ExecProcessCleanupException("bounded process join", exception); }
    }

    protected override void CloseNative() { }

    private async Task ObserveRootOnlyGroupAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = PosixNative.ListGroup(2, (uint)_pid, out var members, 8);
            if (bytes == 4 && members.First == _pid) return;
            if (bytes != 8) throw new ExecProcessCleanupException("observe retained root-only group",
                new IOException($"Group query returned {bytes} bytes, first member {members.First}, OS error {Marshal.GetLastPInvokeError()}."));
            await Task.Delay(10, cancellationToken);
        }
    }

    private bool ObserveRoot()
    {
        var info = Marshal.AllocHGlobal(PosixNative.InfoSize);
        try
        {
            for (var offset = 0; offset < PosixNative.InfoSize; offset += 4) Marshal.WriteInt32(info, offset, 0);
            if (PosixNative.Observe(1, (uint)_pid, info, PosixNative.ExitObservation) == 0)
                return Marshal.ReadInt32(info) != 0;
            var error = Marshal.GetLastPInvokeError();
            if (error == 4) return false;
            if (error == 10) _identityLost = true;
            throw PosixNative.Failure("observe owned root", error);
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    private void ConfigureSpawn(ProcessStartInfo options, IntPtr attributes, IntPtr actions, List<IntPtr> allocations)
    {
        PosixNative.Check(PosixNative.AttributeFlags(attributes, (short)(OperatingSystem.IsMacOS() ? 0x4002 : 2)), "spawn group flags");
        PosixNative.Check(PosixNative.AttributeGroup(attributes, 0), "spawn group");
        PosixNative.Check(PosixNative.Duplicate(actions, Input.ClientSafePipeHandle.DangerousGetHandle().ToInt32(), 0), "stdin action");
        PosixNative.Check(PosixNative.Duplicate(actions, Output.ClientSafePipeHandle.DangerousGetHandle().ToInt32(), 1), "stdout action");
        PosixNative.Check(PosixNative.Duplicate(actions, Error.ClientSafePipeHandle.DangerousGetHandle().ToInt32(), 2), "stderr action");
        PosixNative.Check(PosixNative.ChangeDirectory(actions, Text(options.WorkingDirectory, allocations)), "cwd action");
    }

    private static IntPtr Vector(IEnumerable<string> values, List<IntPtr> allocations)
    {
        var items = values.Select(value => Text(value, allocations)).Append(IntPtr.Zero).ToArray();
        var pointer = Marshal.AllocCoTaskMem(items.Length * IntPtr.Size);
        allocations.Add(pointer);
        Marshal.Copy(items, 0, pointer, items.Length);
        return pointer;
    }

    private static IntPtr Text(string text, List<IntPtr> allocations)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        allocations.Add(pointer);
        return pointer;
    }
}
