using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ForgeMission.Core.Adapters;

internal static class PosixNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupMembers { internal int First, Second; }
    internal static int AttributeSize => OperatingSystem.IsMacOS() ? 8 : 336;
    internal static int ActionSize => OperatingSystem.IsMacOS() ? 8 : 80;
    internal static int InfoSize => OperatingSystem.IsMacOS() ? 104 : 128;
    internal static int ExitObservation => OperatingSystem.IsMacOS() ? 4 | 32 | 1 : 4 | 0x1000000 | 1;

    internal static void Check(int result, string operation)
    {
        if (result != 0) throw new Win32Exception(result, $"{operation} failed (OS error {result}).");
    }

    internal static ExecProcessCleanupException Failure(string operation, int error) =>
        new(operation, new IOException($"{operation} failed (OS error {error})."));

    [DllImport("libc", EntryPoint = "posix_spawnattr_init")] internal static extern int AttributeInit(IntPtr attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")] internal static extern int AttributeFlags(IntPtr attributes, short flags);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")] internal static extern int AttributeGroup(IntPtr attributes, int group);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")] internal static extern int AttributeDestroy(IntPtr attributes);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")] internal static extern int ActionsInit(IntPtr actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")] internal static extern int Duplicate(IntPtr actions, int source, int target);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")] internal static extern int ChangeDirectory(IntPtr actions, IntPtr directory);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")] internal static extern int ActionsDestroy(IntPtr actions);
    [DllImport("libc", EntryPoint = "posix_spawn")] internal static extern int Spawn(out int pid, IntPtr path, IntPtr actions, IntPtr attributes, IntPtr arguments, IntPtr environment);
    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)] internal static extern int Observe(int type, uint pid, IntPtr info, int flags);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)] internal static extern int Reap(int pid, out int status, int flags);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] internal static extern int Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "access", SetLastError = true)] internal static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listpids", SetLastError = true)] internal static extern int ListGroup(uint type, uint group, out GroupMembers members, int bytes);
}
