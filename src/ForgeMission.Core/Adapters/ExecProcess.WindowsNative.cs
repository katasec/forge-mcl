using System.Runtime.InteropServices;
using System.Text;

namespace ForgeMission.Core.Adapters;

internal static class WindowsNative
{
    internal static void Check(bool success, string operation)
    {
        if (!success) throw Failure(operation);
    }

    internal static ExecProcessCleanupException Failure(string operation) =>
        new(operation, new IOException($"{operation} failed (OS error {Marshal.GetLastPInvokeError()})."));

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Info; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal uint Pid, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Accounting
    {
        internal long TotalUser, TotalKernel, PeriodUser, PeriodKernel;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)] internal static extern IntPtr CreateJob(IntPtr security, IntPtr name);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetJob(IntPtr job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeAttributes(IntPtr list, int count, int flags, ref UIntPtr size);
    [DllImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")] internal static extern void DeleteAttributes(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJob(IntPtr job, uint status);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryJob(IntPtr job, int kind, out Accounting accounting, int size, IntPtr returned);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)] internal static extern uint Wait(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ExitCode(IntPtr process, out uint code);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Close(IntPtr handle);
}
